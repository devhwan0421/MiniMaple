using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public class MiniClient : MonoBehaviour
{
    public NetworkManager net;

    int myId = -1;
    public string nickname = "Hwan";    // �α��� �� ���� �г���
    public float lerpSpeed = 15f;   // ��ǥ ���� �ӵ�

    readonly Dictionary<int, Proxy> _players = new Dictionary<int, Proxy>(); // playerid, proxy ����
    readonly Dictionary<int, MProxy> _monsters = new Dictionary<int, MProxy>(); // monsterid,mproxy ����

    Dictionary<PlayerState, int> IndexPair = new();
    List<SPUM_Prefabs> _savedUnitList = new List<SPUM_Prefabs>();
    Dictionary<string, SPUM_Prefabs> _prefabs = new Dictionary<string, SPUM_Prefabs>();

    public InputField idInput;
    public InputField nicknameInput;
    public GameObject LoginPanel;
    public GameObject RespwanPanel;

    private void Awake()
    {
        var saveArray = Resources.LoadAll<SPUM_Prefabs>("");
        foreach (var unit in saveArray)
        {
            if (unit.ImageElement.Count > 0)
            {
                _savedUnitList.Add(unit);
                _prefabs.Add(unit._code, unit);
                unit.PopulateAnimationLists();
            }
        }

        foreach (PlayerState state in Enum.GetValues(typeof(PlayerState)))
        {
            IndexPair[state] = 0;
        }
    }

    void OnEnable() // ������Ʈ Ȱ��ȭ �� ȣ��
    {
        if (net == null)
        {
            //Debug.LogError("[MiniClient] NetworkManager �ʿ�");
            enabled = false;
            return;
        }

        net.OnConnected += HandleConnected; // �̺�Ʈ ����
        net.OnDisconnected += HandleDisconnected;
        net.Onpacket += HandlePacket; // Onpacket �̺�Ʈ�� �Ҹ��� HandlePacket(op, payload)�� ���� ȣ���ϰ�

        //if (net.IsConnected) HandleConnected();
    }

    void OnDisable() // ������Ʈ ��Ȱ��ȭ �� ȣ��
    {
        if (net == null) return;
        net.OnConnected -= HandleConnected;
        net.OnDisconnected -= HandleDisconnected;
        net.Onpacket -= HandlePacket;
    }

    void Update()
    {

        if (Time.frameCount % 6 == 0) // 6�����Ӹ��� �� ��ġ ����
        {
            if (net.IsConnected)
            {
                if(_players.TryGetValue(myId, out var me) && me.go != null)
                {
                    var t = me.go.transform;
                    var rb = me.go.GetComponent<Rigidbody2D>();
                    var pos = (Vector2)t.position;
                    var vel = rb ? rb.linearVelocity : Vector2.zero;

                    //int dir = (t.localScale.x >= 0f) ? 1 : -1;
                    int dir = (me.playerObj.unit.transform.localScale.x > 0) ? 1 : -1;
                    
                    int state = me.playerObj.state;

                    net.SendPose(myId, pos, vel, dir, state);
                    //Debug.Log($"[MiniClient] SendPose name={t.name} scale.x={t.localScale.x} dir={dir} state={state}");
                }
            }
        }

        foreach (var kv in _players) // ��� �÷��̾ ����
        {
            var id = kv.Key;
            if (id == myId) continue; // �� ĳ���ʹ� ���� ����

            var p = kv.Value;
            if (p.go == null) continue; // ������Ʈ�� ������ ��ŵ

            var pos = (Vector2)p.go.transform.position; // ���� ��ġ
            var next = Vector2.Lerp(pos, p.targetPos, Time.deltaTime * lerpSpeed); //����
            p.go.transform.position = new Vector3(next.x, next.y, 0); // �� ��ġ ����
        }

        foreach (var kv in _monsters)
        {
            var p = kv.Value;
            if (p.go == null) continue;
            var pos = (Vector2)p.go.transform.position;
            var next = Vector2.Lerp(pos, p.targetPos, Time.deltaTime * lerpSpeed);
            p.go.transform.position = new Vector3(next.x, next.y, 0);
        }
    }

    void HandleConnected()
    {
        //Debug.Log("[MiniClient] Connected. Sending OP_LOGIN");
        //ServerLogin���� �̵���.
    }

    public void ServerLogin() //�α��ν� ȣ��
    {
        net.SendLogin(idInput.text, nicknameInput.text);
    }

    void HandleDisconnected()
    {
        Debug.LogWarning("[MiniClient] Disconnected.");
        myId = -1;
    }

    void HandlePacket(ushort op, ArraySegment<byte> payload) // ���� ��Ŷ ó��
    {
        var json = Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count);

        switch (op)
        {
            case NetworkManager.OP_LOGIN_OK: // �α��� ���� ����
                {
                    var res = JsonUtility.FromJson<LoginOk>(json);
                    myId = res.id;
                    //Debug.Log($"[MiniClient] LOGIN_OK id={myId}, nick={res.nickname}");
                    EnsureProxy(myId, true, res.nickname);
                    LoginPanel.SetActive(false);
                    break;
                }
            case NetworkManager.OP_STATE: // ��� �÷��̾� ��ǥ �ֱ��� ������Ʈ
                {
                    var arr = JsonArrayHelper.FromJson<PlayerInfo>(json);
                    ApplyState(arr);
                    break;
                }
            case NetworkManager.OP_MSTATE:
                {
                    var arr = JsonArrayHelper.FromJson<MonsterInfo>(json);
                    ApplyMonsters(arr);
                    //Debug.Log($"[MiniClient] OP_MSTATE id={myId}, arr={arr}");
                    break;
                }
            case NetworkManager.OP_HIT:
                {
                    var arr = JsonUtility.FromJson<Hit>(json);
                    //Debug.Log($"[MiniClient] OP_HIT id={myId}, arr={arr}");
                    if (arr.objType == 0) //�˹�� ��ü�� Ÿ���� �÷��̾��� ���
                    {
                        //Debug.Log($"[MiniClient] OP_HIT myid={myId}, type={arr.objType}, objid={arr.objId}, dir={arr.dir}, damage= {arr.damage}");
                        if (arr.objId == myId) //���� �˹�� ���¸� ��ε� ĳ��Ʈ�� ���̹Ƿ� �� �ϰ�� ����
                            break;
                        var player = _players[arr.objId].go;
                        //Debug.Log($"[MiniClient] player init end");
                        var actor = player.GetComponent<Player>();
                        //Debug.Log($"[MiniClient] actor init end [actor={actor}]");
                        if (actor != null)
                        {
                            float knockbackX = 3.0f * arr.dir;
                            float knockbackY = 4.0f * arr.dir;
                            var knock = new Vector2(knockbackX, knockbackY);
                            actor.ApplyHurt(arr.damage, knock);
                            //Debug.Log($"[MiniClient] ApplyHurt");
                        }
                    }
                    else if(arr.objType == 1) // �˹�� ��ü�� Ÿ���� ������ ���
                    {
                        Debug.Log($"[MiniClient] monster hit! id={arr.objId}");
                        var monster = _monsters[arr.objId].go.GetComponent<Monster>();
                        if(monster != null)
                        {
                            //monster.hp -= arr.damage;
                            //�������� ������
                        }
                    }
                    break;
                }
            default:
                break;
        }
    }

    public void SendHitReq(int objType, int objId, int dir, int damage) // �÷��̾����� ��������, �˹�� ��ü, �˹�� ����, ���� ������
    {
        if (net.IsConnected)
        {
            var req = new Hit { objType = objType, objId = objId, dir = dir, damage = damage };
            net.SendJson(NetworkManager.OP_HIT, req);
        }
    }

    void EnsureMineComponents(Proxy proxy, int id)
    {
        var actor = proxy.go.GetComponent<Player>();
        actor.name = $"Player_{id}_Me";
        if (actor == null) actor = proxy.go.AddComponent<Player>();
        actor.playerId = id;
        actor.isMine = true;
        actor.hpImage = GameObject.Find("UI").transform.GetChild(2).GetComponent<Image>();

        var weapon = proxy.go.GetComponent<Weapon>();
            if (weapon == null) weapon = proxy.go.AddComponent<Weapon>();
            weapon.client = this;
            weapon.player = actor;

            if (weapon.hitbox == null)
                //weapon.hitbox = proxy.go.GetComponentInChildren<Collider2D>();
                weapon.hitbox = proxy.go.transform.GetChild(0).gameObject.transform.GetComponentInChildren<Collider2D>();
    }

    // ---------------- State apply ----------------

    void ApplyState(PlayerInfo[] states) // ������ ��ü ���¸� ȭ�鿡 �ݿ�
    {
        if (states == null) return;

        // ���� ǥ��
        var alive = new HashSet<int>(); // �̹� �����ӿ� ����ִ� �÷��̾� ID ����
        foreach (var st in states)
        {
            alive.Add(st.id);
            var proxy = EnsureProxy(st.id, isMine: st.id == myId, st.nickname);
            proxy.targetPos = new Vector2(st.x, st.y);
            proxy.playerObj.currentHp = st.hp;

            if (st.id != myId)
            {
                proxy.playerObj.state = st.state;
                proxy.playerObj.dir = st.dir;
            }
        }

        // ����� �÷��̾� ����
        var toRemove = new List<int>();
        foreach (var kv in _players)
            if (!alive.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var id in toRemove) { Destroy(_players[id].go); _players.Remove(id); }
    }

    void ApplyMonsters(MonsterInfo[] states)
    {
        if (states == null) return;
        var alive = new HashSet<int>();
        foreach(var st in states)
        {
            alive.Add(st.id);
            var m = EnsureMonster(st.id);
            m.targetPos = new Vector2(st.x, st.y);
            m.dir = st.dir;
            if (m.go != null)
            {
                var s = m.go.transform.localScale;
                s.x = Mathf.Abs(s.x) * (st.dir >= 0 ? -1 : 1);
                m.monsterObj = m.go.GetComponent<Monster>();
                m.monsterObj.dir = s;
                m.monsterObj.hp = st.hp;
                m.monsterObj.setHp();
                m.monsterObj.damage = 9;
                //Debug.Log($"hp={st.hp}");
            }
        }

        //despwan
        var toRemove = new List<int>();
        foreach (var kv in _monsters)
            if (!alive.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var id in toRemove) { Destroy(_monsters[id].go); _monsters.Remove(id); }
    }

    Proxy EnsureProxy(int id, bool isMine, string nickname)
    {
        if (_players.TryGetValue(id, out var proxy) && proxy.go != null) // �̹� ������
            return proxy;

        proxy = new Proxy();
        string code = "SPUM_20240911215638389";
        if (_prefabs.TryGetValue(code, out var go))
        {
            SPUM_Prefabs instance = Instantiate(go, Vector2.zero, Quaternion.identity);
            instance.name = isMine ? $"Player_{id}_Me" : $"Player_{id}";
            instance.transform.localScale = new Vector3(2.0f, 2.0f, 1.0f);
            instance.OverrideControllerInit();
            proxy.go = instance.gameObject;
        }

        var player = proxy.go.GetComponent<Player>();
        if (player == null) player = proxy.go.AddComponent<Player>();
        player.playerId = id;
        player.client = this;
        player.setNickname(nickname);

        proxy.playerObj = player;

        _players[id] = proxy; // ��ųʸ��� ���

        if (isMine)
        {
            player.isMine = true;
            Debug.Log($"[MiniClient] isMine={isMine}");
            player.hpImage = GameObject.Find("UI").transform.GetChild(2).GetComponent<Image>();

            //���� ������Ʈ Ž��
            var weaponObj = proxy.go.transform.Find("UnitRoot/Root/BodySet/P_Body/ArmSet/ArmR/P_RArm/P_Weapon/R_Weapon");
            var weapon = weaponObj.gameObject.AddComponent<Weapon>();
            weapon.client = this;
            weapon.player = player;
            weapon.hitbox = weaponObj.GetComponent<Collider2D>();
        }
        return proxy;
    }

    MProxy EnsureMonster(int id)
    {
        if (_monsters.TryGetValue(id, out var mproxy) && mproxy.go != null) // �̹� ������
            return mproxy; // �״�� ��ȯ

        mproxy = new MProxy();
        string code = "SPUM_20240911215637961";
        if (_prefabs.TryGetValue(code, out var go))
        {
            SPUM_Prefabs instance = Instantiate(go, Vector2.zero, Quaternion.identity);
            instance.name = $"Monster_{id}";
            instance.OverrideControllerInit();
            mproxy.go = instance.gameObject;

            var monster = mproxy.go.GetComponent<Monster>();
            if (monster == null) monster = mproxy.go.AddComponent<Monster>();
            monster.monsterId = id;

            var monsterAttack = mproxy.go.GetComponentInChildren<MonsterAttack>();
            monsterAttack.client = this;
            monsterAttack.monster = monster;
        }
        _monsters[id] = mproxy;
        return mproxy;
    }

    public static class JsonArrayHelper
    {
        [Serializable] private class Wrapper<T> { public T[] Items; }
        public static T[] FromJson<T>(string json)
        {
            // Unity JsonUtility�� ��Ʈ �迭�� �� �Ľ��ϹǷ� �����ش�.
            return JsonUtility.FromJson<Wrapper<T>>("{\"Items\":" + json + "}").Items; // {"Items":[...]} ���·� ���� �Ľ�
        }
    }

    class Proxy
    {
        public GameObject go;                   // ���� Unity GameObject(��������Ʈ ����)
        public Player playerObj;
        public Vector2 targetPos;               // ������ ���� ��ǥ ��ǥ(���� Ÿ��)
    }

    class MProxy
    {
        public GameObject go;
        public Monster monsterObj;
        public Vector2 targetPos;
        public int dir;
    }

    [Serializable] 
    class LoginOk {
        public int id;
        public string nickname;
    }

    [Serializable]
    public class PlayerInfo
    {
        public int id;
        public string userid;
        public string nickname;

        public int state;
        public int hp;
        public int level;
        public int exp;

        public int dir;
        public float x;
        public float y;

        public string inventory;
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
    public class Hit // �÷��̾����� ��������, �˹�� ��ü, �˹�� ����, ���� ������
    {
        public int objType;
        public int objId;
        public int dir;
        public int damage;
    }

    [Serializable]
    public class Hurt
    {
        public int victimId;
        public int damage;
        public float knockX;
        public float knockY;
        public float invSec;    // �����ð�
    }
}