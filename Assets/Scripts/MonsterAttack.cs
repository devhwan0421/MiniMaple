using System;
using UnityEngine;

public class MonsterAttack : MonoBehaviour
{
    public MiniClient client;

    public Monster monster;

    public float knockbackX = 3.0f;
    public float knockbackY = 4.0f;

    void Awake()
    {
        //player = GetComponent<Player>();
    }

    void OnTriggerEnter2D(Collider2D other) //
    {
        var player = other.GetComponent<Player>();
        if (player == null) return;
        if (player.IsInvincible() || client == null) return;

        int dir = other.transform.position.x > transform.position.x ? 1 : -1;

        var knock = new Vector2(dir * knockbackX, knockbackY);


        int objType = 0; // 0 : 플레이어, 1: 몬스터
        int objId = player.playerId;
        int objDir = dir;
        //int damage = 10;
        int damage = monster.damage;

        player.ApplyHurt(damage, knock);
        client.SendHitReq(objType, objId, objDir, damage); // 플레이어인지 몬스터인지, 넉백될 객체, 넉백될 방향, 입을 데미지
    }
}