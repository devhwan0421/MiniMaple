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
        //if (!hitbox) Debug.LogWarning("��Ʈ�ڽ� ����");
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

        //���Ͱ� �з��� ���� ���
        int dir = collision.transform.position.x > transform.position.x ? -1 : 1;
        var knock = new Vector2(0.0f, 0.0f); //�������� ó���ϹǷ� 0���� �ѱ�

        int objType = 1; // 0 : �÷��̾�, 1: ����
        int objId = monster.monsterId;
        int objDir = dir;
        int damage = 10;

        monster.ApplyHurt(damage, knock); //���� ������ �ִϸ��̼� ���
        client.SendHitReq(objType, objId, objDir, damage); //���� ������ �޾����� ��ο��� ��ε�ĳ��Ʈ
    }
}
