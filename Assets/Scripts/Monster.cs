using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class Monster : MonoBehaviour {
    public int monsterId;
    public int hp;
    public int maxHp;
    public int damage;
    public Vector3 dir;

    public SPUM_Prefabs spum;
    Dictionary<PlayerState, int> IndexPair = new();

    public GameObject unit;

    public Image hpImage;
    public Text ui_nickname;
    public string nickname;
    public Text damageText;

    bool isInit = false;

    bool _knockback = false;
    public float knockDuration = 0.8f;

    void Awake()
    {
        //��ü ��� �� �ִϸ��̼� �ʱ�ȭ
        if (spum == null) spum = GetComponent<SPUM_Prefabs>();
        //spum.OverrideControllerInit();
        foreach (PlayerState state in Enum.GetValues(typeof(PlayerState)))
        {
            IndexPair[state] = 0;
        }
        spum.PlayAnimation(PlayerState.ATTACK, IndexPair[PlayerState.ATTACK]);

        //���� ���� �ʱ�ȭ
        unit = transform.GetChild(0).gameObject;

        //������ UI �ʱ�ȭ
        damageText = transform.GetChild(1).gameObject.transform.GetComponentInChildren<Text>();

        //ü�¹� UI �ʱ�ȭ
        hpImage = transform.GetChild(1).gameObject.transform.GetChild(0).gameObject.transform.GetChild(1).gameObject.GetComponent<Image>();
    }

    private void Update()
    {
        if(unit != null)
            unit.transform.localScale = dir;
        if(spum != null && !_knockback)
            spum.PlayAnimation(PlayerState.MOVE, IndexPair[PlayerState.MOVE]);

        if (hp > 0 &&hpImage)
        {
            float result = (float)hp / (float)maxHp;
            //Debug.Log($"hp={hp}, maxhp={maxHp}, result={result}");

            hpImage.fillAmount = result;
        }

        if (hp <= 0)
        {
            Debug.Log("Destroy");
            Destroy(gameObject);
        }
    }

    public void setHp()
    {
        if (!isInit)
        {
            maxHp = hp;
            isInit = true;
        }
    }

    public void ApplyHurt(int damage, Vector2 knock)
    {
        StartCoroutine(CoKnockback(knock));
    }

    IEnumerator CoKnockback(Vector2 knock)
    {
        _knockback = true;
        spum.PlayAnimation(PlayerState.DAMAGED, IndexPair[PlayerState.DAMAGED]);

        // ���� Ÿ�ֿ̹� ���� ���
        float end = Time.time + knockDuration;
        while (Time.time < end)
            yield return new WaitForFixedUpdate();  // ���� ������ �������� ����

        _knockback = false;
    }
}