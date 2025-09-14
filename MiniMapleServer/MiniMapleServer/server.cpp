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
    int dir = 1;       // 1 or -1 (스프라이트 좌우)

    std::string inventory;
};

struct Monster {
    int   id = 0;
    float x = 0, y = 0;
    int   dir = 1;       // 1 or -1
    int   hp = 100;

    //몬스터 이동
    float leftX = -5.f, rightX = 5.f, speed = 2.f;

    float vx = 0.f, vy = 0.f;
    uint64_t knockUntilMs = 0; // 넉백 시간
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
    OP_STATE = 4,   // 플레이어들
    OP_MSTATE = 10,   // 몬스터들  ← 새로 사용
    OP_HIT = 11,
    OP_PING = 100,
    OP_PONG = 101,
    OP_CPOSE = 201, // 클라 권위 좌표 보고
    OP_HIT_REQ = 210, // 클라→서버: 피격 요청
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
                    if (t < m.knockUntilMs) { // 넉백 유지 시간. 현재 800ms로 설정했음
                        m.x += m.vx * 0.01f;  // 이동. m.vx에 넉백 방향과 힘 미리 구해둠.
                        m.vx *= 0.90f;        // 이렇게 하면 감쇠 효과 연출 가능
                        ++it;
                        continue;             // 순찰 보류
                    }

                    m.x += m.dir * m.speed * 0.01f; // 몬스터 이동
                    if (m.x > m.rightX) { m.x = m.rightX; m.dir = -1; } // 우측 도달 시 방향전환
                    if (m.x < m.leftX) { m.x = m.leftX;  m.dir = 1; } // 좌측 도달 시 방향전환
                    ++it;
                }
            }
        }
        ::Sleep(10); //10ms tick
    }
}

bool send_packet(SOCKET s, uint16_t op, const std::string& payload)
{
    const uint32_t len = 2u + static_cast<uint32_t>(payload.size()); // opcode 포함 길이
    std::vector<char> pkt(4 + len);

    // 헤더 작성
    write_u32_le(pkt.data() + 0, len);
    write_u16_le(pkt.data() + 4, op);

    // 페이로드 복사
    if (!payload.empty())
        std::memcpy(pkt.data() + 6, payload.data(), payload.size());

    // 전송이 한번에 되지 않을 수 도 있으니 반복문으로 처리
    const char* buf = pkt.data();
    int total = static_cast<int>(pkt.size());
    int offset = 0;
    while (offset < total) { //offset 만큼 더 전송되게
        int n = ::send(s, buf + offset, total - offset, 0);
        if (n <= 0) return false;
        offset += n;
    }
    //printf("send 전송완료!\n");
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
        
        std::vector<char> payBuf; // 바이트 끊길 수 도 있으니 char로 안전하게
        payBuf.resize(payLen);
        if (payLen > 0 && !recv_n(s, payBuf.data(), payLen)) break;

        std::string payload(payBuf.begin(), payBuf.end());

        if (op == 1) { // 클라이언트 로그인 패킷 수신
            std::string userid = "Userid";
            auto pos = payload.find("\"userid\":\""); // 10자리
            if (pos != std::string::npos) {
                auto start = pos + 10;
                auto end = payload.find("\"", start); // 마지막 인덱스 자리

                //"userid":" 다음부터 마지막인 " 사이의 문자열을 userid에 반환
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
                    //신규
                }
                myId = p.id;
                g_players[s] = p;
                DB[userid] = p; //시간상 보류
            }

            char buf[256];
            snprintf(buf, sizeof(buf), "{\"id\":%d,\"userid\":\"%s\",\"nickname\":\"%s\"}", myId, userid.c_str(), name.c_str());
            //printf("%s\n", buf);
            if (!send_packet(s, 2, buf)) break; // 클라이언트로 OP_LOGIN_OK 패킷 송신
        }
        else if (op == OP_HIT) // 플레이어나 몬스터가 데미지를 받았을 때의 패킷 수신
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

                        // hp 업데이트
                        m.hp = max(0, m.hp - damage);
                        float knockX = 6.f * (float)dir * -1; //넉백 방향 미리 곱함
                        m.vx = knockX;
                        m.dir = dir;

                        // 넉백 유지 시간
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
        else if (op == 201) { // 플레이어 상태 패킷 수신
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

            //플레이어 상태 업데이트
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

    // 클라이언트 종료될 경우 정리
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

        // 플레이어 이동 스냅샷
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

        // 몬스터 이동 스냅샷
        std::string mj = "[";
        for (size_t i = 0;i < msnap.size();++i) {
            char buf[160];
            std::snprintf(buf, sizeof(buf), "{\"id\":%d,\"x\":%.2f,\"y\":%.2f,\"dir\":%d,\"hp\":%d}",
                msnap[i].id, msnap[i].x, msnap[i].y, msnap[i].dir, msnap[i].hp);
            mj += buf; if (i + 1 < msnap.size()) mj += ",";
        }
        mj += "]";

        //std::cout << mj << "\n";

        //브로드 캐스트
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
        printf("접속됨.");
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