#define _WINSOCK_DEPRECATED_NO_WARNINGS
#include <winsock2.h>
#include <windows.h>
#include <cstdio>
#include <thread>
#include <vector>
#include <mutex>
#include <atomic>
#include <unordered_map>
#include <string>
#include <iostream>

#pragma comment(lib, "ws2_32.lib")

struct Player {
    int id = 0;
    std::string userid;
    std::string nickname;

    int state = 0; // 0:idle, 1:move, 2:attack
    int hp = 100;
    int level = 0;
    int exp = 0;

    float x = 0, y = 0;
    float vx = 0, vy = 0;
    int dir = 1;       // 1 or -1 (��������Ʈ �¿�)

    std::string inventory;
};

struct Monster {
    int   id = 0;
    float x = 0, y = 0;
    int   dir = 1;       // 1 or -1
    int   hp = 100;

    //���� �̵�
    float leftX = -5.f, rightX = 5.f, speed = 2.f;

    float vx = 0.f, vy = 0.f;
    uint64_t knockUntilMs = 0; // �˹� �ð�
};

struct Hit {
    int objType = 0;
    int objId = 0;
    int dir = 0;
    int damage = 0;
};

enum : uint16_t {
    OP_LOGIN = 1,
    OP_LOGIN_OK = 2,
    OP_STATE = 4,   // �÷��̾��
    OP_MSTATE = 10,   // ���͵�  �� ���� ���
    OP_HIT = 11,
    OP_PING = 100,
    OP_PONG = 101,
    OP_CPOSE = 201, // Ŭ�� ���� ��ǥ ����
    OP_HIT_REQ = 210, // Ŭ��漭��: �ǰ� ��û
};

std::mutex g_mtx;
std::unordered_map<std::string, Player> DB;
std::unordered_map<SOCKET, Player> g_players;
std::vector<SOCKET> g_clients;
std::atomic<int> g_nextId{ 1 };
std::atomic<bool> g_running{ true };

static void write_u32_le(char* p, uint32_t v) { p[0] = v & 0xff; p[1] = (v >> 8) & 0xff; p[2] = (v >> 16) & 0xff; p[3] = (v >> 24) & 0xff; }
static void write_u16_le(char* p, uint16_t v) { p[0] = v & 0xff; p[1] = (v >> 8) & 0xff; }

std::unordered_map<int, Monster> g_monsters;
std::atomic<int> g_nextMonsterId{ 1 };

static uint64_t now_ms() {
    FILETIME ft; GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER ul; ul.LowPart = ft.dwLowDateTime; ul.HighPart = ft.dwHighDateTime;
    return (ul.QuadPart - 116444736000000000ULL) / 10000ULL; // to ms
}

int spawn_monster(float x, float y, float leftX, float rightX, float speed)
{
    int id = g_nextMonsterId++;
    Monster m;
    m.id = id; m.x = x; m.y = y; m.leftX = leftX; m.rightX = rightX; m.speed = speed; m.dir = 1; m.hp = 100;
    g_monsters[id] = m;
    return id;
}

void monster_ai_thread()
{
    //spawn_monster(-3.4f, 0.1f, -4.5f, 7.5f, 2.0f);
    uint64_t spwan_time = now_ms() + 3000;

    while (g_running) {
        {
            //printf("aaac\n");
            std::lock_guard<std::mutex> lk(g_mtx);
            uint64_t t = now_ms();

            if (g_monsters.size() == 0)
            {
                if (t > spwan_time)
                {
                    int monsterSpawn1 = spawn_monster(-3.4f, 0.1f, -4.5f, 7.5f, 2.0f);
                    int monsterSpawn2 = spawn_monster(7.0f, 4.1f, -4.5f, 7.5f, 2.0f);
                }
            }

            for (auto it = g_monsters.begin(); it != g_monsters.end(); ) {
                Monster& m = it->second;

                //printf("aaa\n");

                if (m.hp <= 0)
                {
                    it = g_monsters.erase(it);
                    spwan_time = now_ms() + 3000;
                    //printf("aaad\n");
                }
                else
                {
                    if (t < m.knockUntilMs) { // �˹� ���� �ð�. ���� 800ms�� ��������
                        m.x += m.vx * 0.01f;  // �̵�. m.vx�� �˹� ����� �� �̸� ���ص�.
                        m.vx *= 0.90f;        // �̷��� �ϸ� ���� ȿ�� ���� ����
                        ++it;
                        continue;             // ���� ����
                    }

                    m.x += m.dir * m.speed * 0.01f; // ���� �̵�
                    if (m.x > m.rightX) { m.x = m.rightX; m.dir = -1; } // ���� ���� �� ������ȯ
                    if (m.x < m.leftX) { m.x = m.leftX;  m.dir = 1; } // ���� ���� �� ������ȯ
                    ++it;
                }
            }
        }
        ::Sleep(10); //10ms tick
    }
}

bool send_packet(SOCKET s, uint16_t op, const std::string& payload)
{
    const uint32_t len = 2u + static_cast<uint32_t>(payload.size()); // opcode ���� ����
    std::vector<char> pkt(4 + len);

    // ��� �ۼ�
    write_u32_le(pkt.data() + 0, len);
    write_u16_le(pkt.data() + 4, op);

    // ���̷ε� ����
    if (!payload.empty())
        std::memcpy(pkt.data() + 6, payload.data(), payload.size());

    // ������ �ѹ��� ���� ���� �� �� ������ �ݺ������� ó��
    const char* buf = pkt.data();
    int total = static_cast<int>(pkt.size());
    int offset = 0;
    while (offset < total) { //offset ��ŭ �� ���۵ǰ�
        int n = ::send(s, buf + offset, total - offset, 0);
        if (n <= 0) return false;
        offset += n;
    }
    //printf("send ���ۿϷ�!\n");
    return true;
}

bool recv_n(SOCKET s, char* buf, int len)
{
    int off = 0;
    while (off < len) {
        int r = ::recv(s, buf + off, len - off, 0);
        if (r <= 0) return false;
        off += r;
    }
    return true;
}

void client_thread(SOCKET s)
{
    while (g_running) {
        char hdr[6];
        if (!recv_n(s, hdr, 6)) break;

        uint32_t len = (unsigned char)hdr[0]
            | ((unsigned char)hdr[1] << 8)
            | ((unsigned char)hdr[2] << 16)
            | ((unsigned char)hdr[3] << 24);
        uint16_t op = (unsigned char)hdr[4]
            | ((unsigned char)hdr[5] << 8);
        //printf("len : %d\nopcode : %u\n", len, op);

        if (len < 2) break;

        int payLen = (int)len - 2;
        
        std::vector<char> payBuf; // ����Ʈ ���� �� �� ������ char�� �����ϰ�
        payBuf.resize(payLen);
        if (payLen > 0 && !recv_n(s, payBuf.data(), payLen)) break;

        std::string payload(payBuf.begin(), payBuf.end());

        if (op == 1) { // Ŭ���̾�Ʈ �α��� ��Ŷ ����
            std::string userid = "Userid";
            auto pos = payload.find("\"userid\":\""); // 10�ڸ�
            if (pos != std::string::npos) {
                auto start = pos + 10;
                auto end = payload.find("\"", start); // ������ �ε��� �ڸ�

                //"userid":" �������� �������� " ������ ���ڿ��� userid�� ��ȯ
                if (end != std::string::npos) userid = payload.substr(start, end - start);
            }
            std::string name = "Player";
            pos = payload.find("\"nickname\":\"");
            if (pos != std::string::npos) {
                auto start = pos + 12;
                auto end = payload.find("\"", start);
                if (end != std::string::npos) name = payload.substr(start, end - start);
            }
            int myId;

            {
                std::lock_guard<std::mutex> lk(g_mtx);
                g_clients.push_back(s);
                Player p;
                p.id = g_nextId++;
                p.userid = userid;
                p.nickname = name;

                auto it = DB.find(userid);
                if (it != DB.end())
                {
                    p.level = it->second.level;
                    p.exp = it->second.exp;
                    p.inventory = it->second.inventory;
                }
                else
                {
                    //�ű�
                }
                myId = p.id;
                g_players[s] = p;
                DB[userid] = p; //�ð��� ����
            }

            char buf[256];
            snprintf(buf, sizeof(buf), "{\"id\":%d,\"userid\":\"%s\",\"nickname\":\"%s\"}", myId, userid.c_str(), name.c_str());
            //printf("%s\n", buf);
            if (!send_packet(s, 2, buf)) break; // Ŭ���̾�Ʈ�� OP_LOGIN_OK ��Ŷ �۽�
        }
        else if (op == OP_HIT) // �÷��̾ ���Ͱ� �������� �޾��� ���� ��Ŷ ����
        {
            auto findInt = [&](const char* key, int def = 0)->int {
                std::string k = std::string("\"") + key + "\":";
                auto p = payload.find(k);
                if (p == std::string::npos) return def;
                p += k.size();
                char* endp = nullptr;
                long v = std::strtol(payload.c_str() + p, &endp, 10);
                return (int)v;
            };

            int objType = findInt("objType");
            int objId = findInt("objId");
            int dir = findInt("dir");
            int damage = findInt("damage", 10);

            if (objType == 0)
            {
                //printf("objtype=0!\n");
                {
                    std::lock_guard<std::mutex> lk(g_mtx);

                    for (auto& it : g_players) {
                        Player& m = it.second;
                        if (m.id == objId)
                        {
                            printf("=== m.hp : %d\n", m.hp);
                            m.hp = max(0, m.hp - damage);
                            printf("=== m.hp : %d\n", m.hp);
                        }
                    }

                }
            }

            if (objType == 1)
            {
                {
                    std::lock_guard<std::mutex> lk(g_mtx);
                    auto it = g_monsters.find(objId);
                    if (it != g_monsters.end()) {
                        Monster& m = it->second;

                        // hp ������Ʈ
                        m.hp = max(0, m.hp - damage);
                        float knockX = 6.f * (float)dir * -1; //�˹� ���� �̸� ����
                        m.vx = knockX;
                        m.dir = dir;

                        // �˹� ���� �ð�
                        m.knockUntilMs = now_ms() + 800; // 800ms
                    }
                }
            }
            printf("objType:%d, objId:%d, dir:%d, damage:%d!\n", objType, objId, dir, damage);

            char buf[160];
            std::snprintf(buf, sizeof(buf),
                "{\"objType\":%d,\"objId\":%d,\"dir\":%d,\"damage\":%d}",
                objType, objId, dir, damage);

            std::vector<SOCKET> clients;
            { 
                std::lock_guard<std::mutex> lk(g_mtx); 
                clients = g_clients; 
            }
            for (auto cs : clients) 
                send_packet(cs, OP_HIT, buf);
        }
        else if (op == 100) { // OP_PING
            send_packet(s, 101, "{}"); // OP_PONG
        }
        else if (op == 201) { // �÷��̾� ���� ��Ŷ ����
            auto findFloat = [&](const std::string& key, float def = 0.f) -> float {
                std::string k = std::string("\"") + key + "\":";
                auto p = payload.find(k);
                if (p == std::string::npos) return def;
                p += k.size();
                char* endp = nullptr;
                return std::strtof(payload.c_str() + p, &endp);
            };
            auto findInt = [&](const std::string& key, int def = 0) -> int {
                std::string k = std::string("\"") + key + "\":";
                auto p = payload.find(k);
                if (p == std::string::npos) return def;
                p += k.size();
                char* endp = nullptr;
                long v = std::strtol(payload.c_str() + p, &endp, 10);
                return (int)v;
            };

            float state = findFloat("state");
            float x = findFloat("x");
            float y = findFloat("y");
            float vx = findFloat("vx");
            float vy = findFloat("vy");
            int dir = findInt("dir");

            //�÷��̾� ���� ������Ʈ
            {
                std::lock_guard<std::mutex> lk(g_mtx);
                auto it = g_players.find(s);
                if (it != g_players.end()) {
                    it->second.state = state;
                    it->second.x = x;
                    it->second.y = y;
                    it->second.vx = vx;
                    it->second.vy = vy;
                    it->second.dir = dir;
                }
            }
        }
    }

    // Ŭ���̾�Ʈ ����� ��� ����
    {
        std::lock_guard<std::mutex> lk(g_mtx);
        g_players.erase(s);
        g_clients.erase(std::remove(g_clients.begin(), g_clients.end(), s), g_clients.end());
    }
    closesocket(s);
}

void broadcaster_thread()
{
    printf("[Server] broadcaster_thread start\n");
    int tick = 0;

    while (g_running) {
        std::vector<SOCKET> clients;
        std::vector<Player> snapshot;
        std::vector<Monster> msnap;

        {
            std::lock_guard<std::mutex> lk(g_mtx);
            clients = g_clients;
            snapshot.reserve(g_players.size());
            for (auto& kv : g_players) snapshot.push_back(kv.second);
            msnap.reserve(g_monsters.size());
            for (auto& kv : g_monsters) msnap.push_back(kv.second);
        }

        // �÷��̾� �̵� ������
        std::string json;
        json.reserve(64 * (snapshot.size() + 1));
        json += "[";
        for (size_t i = 0; i < snapshot.size(); ++i) {
            char buf[128];
            std::snprintf(buf, sizeof(buf), // "{    \"id\":%d,\"nickname\":\"%s\"    }"
                "{\"id\":%d,\"nickname\":\"%s\",\"state\":%d,\"hp\":%d,\"x\":%.2f,\"y\":%.2f,\"dir\":%d}",
                snapshot[i].id, snapshot[i].nickname.c_str(), snapshot[i].state, snapshot[i].hp, snapshot[i].x, snapshot[i].y, snapshot[i].dir);
            //printf("id:%d, hp:%d ,x:%.2f,y:d%.2f,dir:%d\n", snapshot[i].id, snapshot[i].hp, snapshot[i].x, snapshot[i].y, snapshot[i].dir);
            //printf("%s\n", buf);
            json += buf;
            if (i + 1 < snapshot.size()) json += ",";
        }
        json += "]";

        // ���� �̵� ������
        std::string mj = "[";
        for (size_t i = 0;i < msnap.size();++i) {
            char buf[160];
            std::snprintf(buf, sizeof(buf), "{\"id\":%d,\"x\":%.2f,\"y\":%.2f,\"dir\":%d,\"hp\":%d}",
                msnap[i].id, msnap[i].x, msnap[i].y, msnap[i].dir, msnap[i].hp);
            mj += buf; if (i + 1 < msnap.size()) mj += ",";
        }
        mj += "]";

        //std::cout << mj << "\n";

        //��ε� ĳ��Ʈ
        int okCount = 0, failCount = 0;
        for (auto s : clients) {
            if (send_packet(s, /*OP_STATE=*/4, json)) ++okCount; else ++failCount;
            send_packet(s, OP_MSTATE, mj);
        }
        if (failCount > 0) {
            printf("[Server] broadcast result ok=%d fail=%d\n", okCount, failCount);
        }

        ++tick;
        ::Sleep(10);
    }
}

int main()
{
    WSADATA w; 
    int r = WSAStartup(MAKEWORD(2, 2), &w);
    if (r) return 0;

    SOCKET ls = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in addr{}; addr.sin_family = AF_INET; addr.sin_addr.s_addr = INADDR_ANY; addr.sin_port = htons(7777);
    bind(ls, (sockaddr*)&addr, sizeof(addr));
    listen(ls, SOMAXCONN);

    std::thread bc(broadcaster_thread);
    std::thread mai(monster_ai_thread);

    printf("Listening 0.0.0.0:7777\n");
    while (g_running) {
        SOCKET s = accept(ls, nullptr, nullptr);
        printf("���ӵ�.");
        if (s == INVALID_SOCKET) break;
        std::thread(client_thread, s).detach();
    }

    g_running = false;
    mai.join();
    bc.join();
    closesocket(ls);
    WSACleanup();
    return 0;
}