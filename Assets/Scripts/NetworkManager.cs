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

    public const ushort OP_MSTATE = 10; // ���� ���� �迭 ���
    public const ushort OP_HIT = 11; // Ŭ��漭��, ���� ���Ϳ� �¾Ҵٰ� �Ű�(�Ǵ� ���� �����)
    //public const ushort OP_HURT = 12; // ������Ŭ��, ������/�˹� ���� ����

    public const ushort OP_CPOSE = 201;  // Client -> Server: �� ��ǥ

    //�̺�Ʈ (Unity ���� �����忡�� ȣ��)
    public event Action OnConnected;
    public event Action OnDisconnected;
    public event Action<ushort, ArraySegment<byte>> Onpacket; // opcode, payload

    Socket _sock;
    NetworkStream _ns;
    CancellationTokenSource _cts;   //�����ū�� �����ϰ� �����ϴµ� ���. Task �񵿱� �۾� ������ �۾��� ����ϰų� ��� ���� Ȯ��
    Task _recvTask, _sendTask;
    readonly ConcurrentQueue<byte[]> _sendQ = new();    // �۽� ��� ť (������ ���� ť)
    readonly AutoResetEvent _sendSignal = new(false);   // �۽� �����带 ����� ��ȣ��

    readonly MemoryStream _in = new(64 * 1024);     
    readonly byte[] _buf = new byte[32 * 1024];
    readonly ConcurrentQueue<Action> _mainQ = new();       // ���� �����忡�� ������ �۾� ť

    public bool IsConnected { get; private set; }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);  // ���� �ٲ� �ı����� �ʰ�
        Application.runInBackground = true;  // ��Ŀ�� ��� Update ����
        Application.targetFrameRate = 60;    // �����ӷ���Ʈ ����
        Screen.SetResolution(960, 540, false);
    }

    void Update()
    {
        while (_mainQ.TryDequeue(out var a)) a();   // �ٸ� �����忡�� ť�� �־�� �۾�(Action)�� ���� �����忡�� ����
                                                    // ���ν����� : ����Ƽ ������ ���� ������ ������ �� ������
    }

    public async void Connect()
    {
        Close();
        _cts = new CancellationTokenSource(); // ��� ��ū ���� ����
        try
        {
            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; //NoDelay
            await _sock.ConnectAsync(IPAddress.Parse(host), port);
            _ns = new NetworkStream(_sock, true); // ������ ��Ʈ������ ���μ� ���� �б� ���ϰ�
            IsConnected = true;
            EnqueueMain(() => OnConnected?.Invoke()); // ���� �����忡�� OnConnected �̺�Ʈ ȣ��

            // ����/�۽� ������ ��׶��� Task�� ����
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
        var json = JsonUtility.ToJson(obj); // C# ��ü�� JSON ���ڿ��� ����ȭ
        var body = Encoding.UTF8.GetBytes(json); // ���ڿ� ����Ʈ �迭��

        var len = 2 + body.Length;                // opcode(2) + payload
        var pkt = new byte[4 + len];

        WriteU32(pkt, 0, (uint)len);              // [0..3]�� len ��� (��Ʋ�����)
        WriteU16(pkt, 4, op);                     // [4..5]�� opcode ��� (��Ʋ�����)
        Buffer.BlockCopy(body, 0, pkt, 6, body.Length); // ���� ĭ�� payload ����

        _sendQ.Enqueue(pkt);    // �۽� ť�� �ְ�
        _sendSignal.Set();      // �۽� ������ �����

        //Debug.Log($"[Net] >>> op={op}, body={body.Length}, total={pkt.Length}, json={json}");
    }

    void SendLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_sendQ.IsEmpty) _sendSignal.WaitOne(100); // ť�� ������� 100ms ��ٸ� �� ���
                while (_sendQ.TryDequeue(out var p))
                {
                    //Debug.Log($"[Net] _ns.write");
                    _ns.Write(p, 0, p.Length);  // sendť���� ���� �����͸� ���� ������ ����
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
                int n = _ns.Read(_buf, 0, _buf.Length); // ���Ͽ��� ����Ʈ �б�
                if (n <= 0) { Debug.LogWarning("[Net] Remote closed (n<=0)"); break; }
                //Debug.Log($"[Net] <<< {n} bytes");

                // ���� �����͸� _in ��Ʈ�� �ڿ� �̾� ����
                _in.Position = _in.Length;  //position : ��Ʈ�� �� ������ġ
                _in.Write(_buf, 0, n);  //_buf�� 0���� n���� ��Ʈ���� ��
                _in.Position = 0;

                while (true)
                {
                    if (_in.Length - _in.Position < 6) break; // ���(6����Ʈ) �����ϸ� �ߴ�
                    uint len = ReadU32(_in); // [0..3] ����
                    ushort op = ReadU16(_in); // [4..5] opcode
                    if (len < 2) throw new InvalidDataException();

                    long remain = _in.Length - _in.Position;
                    if (remain < len - 2) {
                        // payload �� �������� ����� �ǵ����� ���� recv���� ��ٸ�
                        _in.Position -= 6;
                        break;
                    }
                    int payLen = (int)len - 2; // ���ڵ� 2 ��ŭ �� ��
                    var payload = payLen > 0 ? ReadBytes(_in, payLen) : Array.Empty<byte>(); //payload�� �����Ѵٸ� ReadBytes ȣ��

                    EnqueueMain(() => Onpacket?.Invoke(op, new ArraySegment<byte>(payload))); // ���� ������ ť�� ���� �̺�Ʈ �ֱ�
                }

                var leftover = _in.Length - _in.Position;
                if(leftover > 0) //_in ��Ʈ���� ���� �����Ͱ� �������
                {
                    var tmp = new byte[leftover];
                    _in.Read(tmp, 0, (int)leftover); //���� ������ tmp�� ����
                    
                    // _in ��Ʈ�� �ʱ�ȭ
                    _in.SetLength(0);
                    _in.Position = 0;

                    // tmp�� ������ �״� �� �ٽ� _in���� ����
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
        EnqueueMain(() => OnDisconnected?.Invoke()); // while���� ������ ���
    }

    void EnqueueMain(Action a) => _mainQ.Enqueue(a);

    // ��Ʋ��������� ��ȯ
    static void WriteU32(byte[] b, int o, uint v) {
        b[o] = (byte)v; //v�� 32��Ʈ�̳� byte ����ȯ�Ͽ��� ������ ������ 8��Ʈ�� ����
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

    static byte[] ReadBytes(Stream s, int len) //��Ʈ������ ������ ���̸�ŭ ����Ʈ�� ����
    {
        var r = new byte[len];
        int off = 0;
        while(off < len) // �� ���� ������ �ݺ�. �дٰ� ���� �� �־ �̷� ������ ������.
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
        public float knockX;    // �˹� ����
        public float knockY;
        public float invSec;    // �����ð�
    }
}