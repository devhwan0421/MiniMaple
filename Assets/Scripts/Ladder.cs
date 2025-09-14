using UnityEngine;
using UnityEngine.Tilemaps;

public class Ladder :MonoBehaviour
{
    Tilemap tilemap;
    Collider2D col;

    private void Awake()
    {
        tilemap = GetComponent<Tilemap>();
        col = GetComponent<Collider2D>();
    }

    public float GetSnapX(Vector3 worldPos)
    {
        if (tilemap)
        {
            var cell = tilemap.WorldToCell(worldPos);
            return tilemap.GetCellCenterWorld(cell).x;
        }
        return col ? col.bounds.center.x : transform.position.x;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other == null) return;
        
        var player = other.GetComponent<Player>();
        if (player)
        {
            //Debug.Log("사다리 들어옴");
            player.canClimb = true;

            /*Vector3Int cellPos = tilemap.WorldToCell(other.bounds.center);
            Vector3 worldPos = tilemap.GetCellCenterWorld(cellPos);*/
        }
        
    }
    private void OnTriggerExit2D(Collider2D other)
    {
        if (other == null) return;
        var player = other.GetComponent<Player>();
        if (player)
        {
            player.canClimb = false;
            player.isClimbing = false;
            player._rb.gravityScale = 3.0f;
            player.gameObject.layer = 10;
            //player.currentLadder = null;
            //Debug.Log("사다리 나감");
        }
    }
}
