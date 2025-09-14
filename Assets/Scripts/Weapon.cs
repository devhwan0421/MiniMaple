using UnityEngine;

public class Weapon : MonoBehaviour
{
    public MiniClient client;
    public Player player;
    public Collider2D hitbox;

    public float hitboxActiveTime = 0.55f;
    public int damage = 5;

    bool attack;
    bool attacking;
    float until;

    void Awake()
    {
        //if (!player) player = GetComponent<Player>();
        //if (!hitbox) Debug.LogWarning("히트박스 없음");
        if (hitbox) hitbox.enabled = false;
    }

    private void Update()
    {
        if(Input.GetKey(KeyCode.LeftControl) && !attack)
        {
            StartAttack();
        }

        if(attack && Time.time >= until)
        {
            attack = false;
            if (hitbox) hitbox.enabled = false;
            attacking = false;
            player.attacking = false;
        }
    }

    void StartAttack()
    {
        attack = true;
        player.AttackAnim();
        until = Time.time + hitboxActiveTime;
        if (hitbox) hitbox.enabled = true;
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (!attack || attacking || client == null || player == null) return;

        attacking = true;

        var monster = collision.GetComponent<Monster>();
        if (monster == null) return;

        //몬스터가 밀려날 방향 계산
        int dir = collision.transform.position.x > transform.position.x ? -1 : 1;
        var knock = new Vector2(0.0f, 0.0f); //서버에서 처리하므로 0으로 넘김

        int objType = 1; // 0 : 플레이어, 1: 몬스터
        int objId = monster.monsterId;
        int objDir = dir;
        int damage = 10;

        monster.ApplyHurt(damage, knock); //몬스터 데미지 애니메이션 재생
        client.SendHitReq(objType, objId, objDir, damage); //몬스터 데미지 받았음을 모두에게 브로드캐스트
    }
}
