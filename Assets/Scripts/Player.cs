using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class Player :MonoBehaviour
{
    public int playerId;
    public bool isMine = false;
    public float invincibleSec = 1.0f;
    bool _knockback = false;
    public float knockDuration = 0.25f;

    public int currentHp = 100;
    public int maxHp = 100;
    public bool isDeath = false;
    public Image hpImage;
    public Text ui_nickname;
    public string nickname;

    public Rigidbody2D _rb;
    bool _invincible;
    float _invUntil;

    public float moveSpeed = 5f;
    public float jumpForce = 13f;
    public GroundCheck groundCheck;

    public GameObject unit;

    public SPUM_Prefabs spum;
    Dictionary<PlayerState, int> IndexPair = new();

    public MiniClient client;

    public int state = 0;
    public int dir = 0;
    public bool attacking = false;

    public bool canClimb = false;
    public bool isClimbing = false;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.gravityScale = 3f;
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;

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

        ui_nickname = transform.GetChild(2).GetComponentInChildren<Text>();

        groundCheck = transform.GetComponentInChildren<GroundCheck>();
    }

    void Update()
    {
        if (isDeath) return;

        if (currentHp > 0 && hpImage)
        {
            float result = (float)currentHp / (float)maxHp;
            hpImage.fillAmount = result;
        }

        // ���� ���� üũ
        _invincible = (Time.time < _invUntil);

        if (_invincible)
        {
            //�˹� �����ð� ���� ������ �ڵ�
        }

        if (!_knockback && isMine && !attacking) //�˹�, ���� �� �̵� �Ұ�
        {
            int ax = (Input.GetAxisRaw("Horizontal") > 0.5f) ? 1 : (Input.GetAxisRaw("Horizontal") < -0.5f ? -1 : 0);
            float vy = Input.GetAxisRaw("Vertical");
            bool jump = Input.GetKeyDown(KeyCode.LeftAlt);

            if (canClimb) //��ٸ��� Ż �� �ִ� �����̰�
            {
                if(vy != 0) // �� �Ʒ� Ű�� ���ȴٸ�
                {
                    isClimbing = true;
                }
            }

            if (canClimb && isClimbing) //��ٸ��� Ÿ�� ���� ���
            {
                state = 2;
                this.gameObject.layer = 22;
                var p = transform.position;
                p.x = 6.5f;
                transform.position = p;
                spum.PlayAnimation(PlayerState.IDLE, IndexPair[PlayerState.IDLE]);
                _rb.linearVelocity = Vector2.zero;
                _rb.gravityScale = 0f;
                Vector3 dir = new Vector3(0, vy, 0).normalized;
                Debug.Log($"dir={dir}");
                transform.Translate(dir * 3.0f * Time.deltaTime);
            }
            else // ������ ���
            {
                var v = _rb.linearVelocity;
                v.x = ax * moveSpeed;
                _rb.linearVelocity = v;

                if (ax != 0)
                {
                    //Debug.Log("ax!=0");
                    state = 1;
                    spum.PlayAnimation(PlayerState.MOVE, IndexPair[PlayerState.MOVE]);

                    var s = unit.transform.localScale;
                    s.x = Mathf.Abs(s.x) * (ax < 0 ? 1 : -1);
                    unit.transform.localScale = s;
                    //Debug.Log($"s.x={s.x}, ax={ax}");
                }
                else
                {
                    state = 0;
                    spum.PlayAnimation(PlayerState.IDLE, IndexPair[PlayerState.IDLE]);
                }

                if (jump && groundCheck.isGrounded)
                    _rb.AddForce(Vector2.up * jumpForce, ForceMode2D.Impulse);
            }
        }
        else if(!_knockback && !isMine)
        {
            //Debug.Log($"dir={dir}");
            var s = unit.transform.localScale;
            s.x = Mathf.Abs(s.x) * (dir < 0 ? -1 : 1);
            unit.transform.localScale = s;

            if (state == 0)
            {
                spum.PlayAnimation(PlayerState.IDLE, IndexPair[PlayerState.IDLE]);
            }
            else if (state == 1)
            {
                spum.PlayAnimation(PlayerState.MOVE, IndexPair[PlayerState.MOVE]);
            }
            else if (state == 2)
            {
                this.gameObject.layer = 22;
                var v = _rb.linearVelocity;
                v.x = 0;
                v.y = 0;
                _rb.linearVelocity = v;
                _rb.gravityScale = 0f;
                spum.PlayAnimation(PlayerState.IDLE, IndexPair[PlayerState.IDLE]);
            }
            else if(state == 3)
            {
                spum.PlayAnimation(PlayerState.ATTACK, IndexPair[PlayerState.ATTACK]);
            }
        }
    }

    public void AttackAnim()
    {
        spum.PlayAnimation(PlayerState.ATTACK, IndexPair[PlayerState.ATTACK]);
        attacking = true;
        state = 3;
    }

    public void setNickname(string nick)
    {
        nickname = nick;
        ui_nickname.text = nickname;
    }

    public void ApplyHurt(int damage, Vector2 knock)
    {
        if (isDeath) return;

        StartCoroutine(CoKnockback(knock));
        ApplyDamage(damage);
    }

    IEnumerator CoKnockback(Vector2 knock)
    {
        _knockback = true;
        spum.PlayAnimation(PlayerState.DAMAGED, IndexPair[PlayerState.DAMAGED]);
        
        _rb.linearVelocity = Vector2.zero;
        _rb.AddForce(knock, ForceMode2D.Impulse);

        // ���� Ÿ�̸�
        _invUntil = Time.time + invincibleSec;

        // ���
        float end = Time.time + knockDuration;
        while (Time.time < end)
            yield return new WaitForFixedUpdate();
        
        _knockback = false;
    }

    public void ApplyDamage(int dmg)
    {
        if (_invincible) return; // ���� ���̸� ������ ����

        currentHp -= dmg;

        if(currentHp <= 0)
        {
            transform.GetChild(0).gameObject.SetActive(false);
            transform.GetChild(1).gameObject.SetActive(true);
            isDeath = true;
            if(isMine)
                client.RespwanPanel.SetActive(true);
        }

        if (isMine)
        {
            float result = (float)dmg / (float)maxHp;
            Debug.Log($"ApplyDamage! dmg={dmg}, currentHp={currentHp}, maxHp={maxHp}, damage={result}");

            hpImage.fillAmount -= result;
        }
    }

    public bool IsInvincible() => _invincible;
}