using UnityEngine;
using UnityEngine.UI;
using System.Net.Sockets;
using System;
using System.Text;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Collections.Generic;

public class NetworkManager : MonoBehaviour
{
    [Header("Endpoint")]
    public string host = "127.0.0.1";
    public int port = 7777;

    public const ushort OP_LOGIN = 1;
    public const ushort OP_LOGIN_OK = 2;
    public const ushort OP_INPUT = 3;
    public const ushort OP_STATE = 4;
    public const ushort OP_PING = 100;
    public const ushort OP_PONG = 101;

    public const ushort OP_MSTATE = 10; // 몬스터 상태 배열 방송
    public const ushort OP_HIT = 11; // 클라→서버, 내가 몬스터에 맞았다고 신고(또는 내가 맞췄다)
    //public const ushort OP_HURT = 12; // 서버→클라, 데미지/넉백 적용 지시

    public const ushort OP_CPOSE = 201;  // Client -> Server: 내 좌표

    //이벤트 (Unity 메인 스레드에서 호출)
    public event Action OnConnected;
    public event Action OnDisconnected;
    public event Action<ushort, ArraySegment<byte>> Onpacket; // opcode, payload

    Socket _sock;
    NetworkStream _ns;
    CancellationTokenSource _cts;   //취소토큰을 생성하고 관리하는데 사용. Task 비동기 작업 내에서 작업을 취소하거나 취소 상태 확인
    Task _recvTask, _sendTask;
    readonly ConcurrentQueue<byte[]> _sendQ = new();    // 송신 대기 큐 (스레드 안전 큐)
    readonly AutoResetEvent _sendSignal = new(false);   // 송신 스레드를 깨우는 신호등

    readonly MemoryStream _in = new(64 * 1024);     
    readonly byte[] _buf = new byte[32 * 1024];
    readonly ConcurrentQueue<Action> _mainQ = new();       // 메인 스레드에서 실행할 작업 큐

    public bool IsConnected { get; private set; }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);  // 씬이 바뀌어도 파괴되지 않게
        Application.runInBackground = true;  // 포커스 없어도 Update 돌려
        Application.targetFrameRate = 60;    // 프레임레이트 고정
        Screen.SetResolution(960, 540, false);
    }

    void Update()
    {
        while (_mainQ.TryDequeue(out var a)) a();   // 다른 스레드에서 큐에 넣어둔 작업(Action)을 메인 스레드에서 실행
                                                    // 메인스레드 : 유니티 엔진이 게임 루프를 돌리는 주 스레드
    }

    public async void Connect()
    {
        Close();
        _cts = new CancellationTokenSource(); // 취소 토큰 새로 생성
        try
        {
            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; //NoDelay
            await _sock.ConnectAsync(IPAddress.Parse(host), port);
            _ns = new NetworkStream(_sock, true); // 소켓을 스트림으로 감싸서 쓰고 읽기 편하게
            IsConnected = true;
            EnqueueMain(() => OnConnected?.Invoke()); // 메인 스레드에서 OnConnected 이벤트 호출

            // 수신/송신 루프를 백그라운드 Task로 실행
            _recvTask = Task.Run(() => RecvLoop(_cts.Token));
            _sendTask = Task.Run(() => SendLoop(_cts.Token));
        }
        catch (Exception e)
        {
            Debug.LogWarning("Connect fail " + e.Message);
            IsConnected = false;
            EnqueueMain(() => OnDisconnected?.Invoke());
        }
    }

    public void Close()
    {
        try { _cts.Cancel(); } catch { }
        try { _ns?.Close(); } catch { }
        try { _sock?.Close(); } catch { }
        _ns = null; _sock = null; _cts = null;
        IsConnected = false;
    }

    void OnEnable() {
        Connect();
    }
    void OnDisable() => Close();

    public void SendLogin(string id, string nickname)
    {
        var req = new LoginReq { userid = id,  nickname = nickname };
        var json = JsonUtility.ToJson(req);
        //Debug.Log($"[Net] >>> LOGIN json={json}");
        SendJson(OP_LOGIN, req);   // OP_LOGIN = 1
    }

    [Serializable]
    public class ClientPose
    {
        public int id;    // myId
        public float x, y;
        public float vx, vy;
        public int dir;
        public int state;
    }

    public void SendPose(int id, Vector2 pos, Vector2 vel, int dir, int state)
    {
        if (!IsConnected) return;
        var p = new ClientPose { id = id, x = pos.x, y = pos.y, vx = vel.x, vy = vel.y, dir = dir, state = state };
        SendJson(OP_CPOSE, p);
    }

    public void SendInput(int seq, int ax, int ay, bool jump)
    {
        var req = new InputReq { seq = seq, ax = ax, ay = ay, jump = jump };
        var json = JsonUtility.ToJson(req);
        //Debug.Log($"[Net] >>> INPUT seq={seq} ax={ax} ay={ay} jump={jump} json={json}");
        SendJson(NetworkManager.OP_INPUT, req);
    }

    public void SendJson(ushort op, object obj)
    {
        var json = JsonUtility.ToJson(obj); // C# 객체를 JSON 문자열로 직렬화
        var body = Encoding.UTF8.GetBytes(json); // 문자열 바이트 배열로

        var len = 2 + body.Length;                // opcode(2) + payload
        var pkt = new byte[4 + len];

        WriteU32(pkt, 0, (uint)len);              // [0..3]에 len 기록 (리틀엔디안)
        WriteU16(pkt, 4, op);                     // [4..5]에 opcode 기록 (리틀엔디안)
        Buffer.BlockCopy(body, 0, pkt, 6, body.Length); // 다음 칸에 payload 복사

        _sendQ.Enqueue(pkt);    // 송신 큐에 넣고
        _sendSignal.Set();      // 송신 스레드 깨우기

        //Debug.Log($"[Net] >>> op={op}, body={body.Length}, total={pkt.Length}, json={json}");
    }

    void SendLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_sendQ.IsEmpty) _sendSignal.WaitOne(100); // 큐가 비었으면 100ms 기다림 후 대기
                while (_sendQ.TryDequeue(out var p))
                {
                    //Debug.Log($"[Net] _ns.write");
                    _ns.Write(p, 0, p.Length);  // send큐에서 꺼낸 데이터를 실제 서버로 전송
                }
                    
            }
        }
        catch { }
    }

    void RecvLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = _ns.Read(_buf, 0, _buf.Length); // 소켓에서 바이트 읽기
                if (n <= 0) { Debug.LogWarning("[Net] Remote closed (n<=0)"); break; }
                //Debug.Log($"[Net] <<< {n} bytes");

                // 읽은 데이터를 _in 스트림 뒤에 이어 붙임
                _in.Position = _in.Length;  //position : 스트림 내 현재위치
                _in.Write(_buf, 0, n);  //_buf의 0부터 n까지 스트림에 씀
                _in.Position = 0;

                while (true)
                {
                    if (_in.Length - _in.Position < 6) break; // 헤더(6바이트) 부족하면 중단
                    uint len = ReadU32(_in); // [0..3] 길이
                    ushort op = ReadU16(_in); // [4..5] opcode
                    if (len < 2) throw new InvalidDataException();

                    long remain = _in.Length - _in.Position;
                    if (remain < len - 2) {
                        // payload 덜 들어왔으면 헤더를 되돌리고 다음 recv까지 기다림
                        _in.Position -= 6;
                        break;
                    }
                    int payLen = (int)len - 2; // 옵코드 2 만큼 뺀 것
                    var payload = payLen > 0 ? ReadBytes(_in, payLen) : Array.Empty<byte>(); //payload가 존재한다면 ReadBytes 호출

                    EnqueueMain(() => Onpacket?.Invoke(op, new ArraySegment<byte>(payload))); // 메인 스레드 큐에 수신 이벤트 넣기
                }

                var leftover = _in.Length - _in.Position;
                if(leftover > 0) //_in 스트림에 남은 데이터가 있을경우
                {
                    var tmp = new byte[leftover];
                    _in.Read(tmp, 0, (int)leftover); //남은 데이터 tmp에 복사
                    
                    // _in 스트림 초기화
                    _in.SetLength(0);
                    _in.Position = 0;

                    // tmp에 복사해 뒀던 것 다시 _in으로 쓰기
                    _in.Write(tmp, 0, tmp.Length);
                    _in.Position = 0;
                }
                else
                {
                    _in.SetLength(0);
                    _in.Position = 0;
                }
            }
        }
        catch { }
        EnqueueMain(() => OnDisconnected?.Invoke()); // while문을 나왔을 경우
    }

    void EnqueueMain(Action a) => _mainQ.Enqueue(a);

    // 리틀엔디안으로 변환
    static void WriteU32(byte[] b, int o, uint v) {
        b[o] = (byte)v; //v는 32비트이나 byte 형변환하였기 때문에 마지막 8비트만 읽음
        b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16);
        b[o + 3] = (byte)(v >> 24);
    }

    static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
    }

    static uint ReadU32(Stream s)
    {
        int b0 = s.ReadByte(), b1 = s.ReadByte(), b2 = s.ReadByte(), b3 = s.ReadByte();
        return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
    }

    static ushort ReadU16(Stream s)
    {
        int b0 = s.ReadByte(), b1 = s.ReadByte();
        return (ushort)(b0 | (b1 << 8));
    }

    static byte[] ReadBytes(Stream s, int len) //스트림에서 지정한 길이만큼 바이트를 읽음
    {
        var r = new byte[len];
        int off = 0;
        while(off < len) // 다 읽을 때까지 반복. 읽다가 끊길 수 있어서 이런 구조를 쓴다함.
        {
            int n = s.Read(r, off, len - off);
            if (n <= 0) throw new EndOfStreamException();
            off += n;
        }
        return r;
    }

    [Serializable]
    public class LoginReq {
        public string userid;
        public string nickname; 
    }

    [Serializable]
    public class InputReq
    {
        public int seq;
        public int ax;
        public int ay;
        public bool jump;
    }

    [Serializable]
    public class MonsterInfo
    {
        public int id;
        public float x;
        public float y;
        public int dir;
        public int hp;
    }

    [Serializable]
    public class Hurt
    {
        public int victimId;
        public int damage;
        public float knockX;    // 넉백 벡터
        public float knockY;
        public float invSec;    // 무적시간
    }
}