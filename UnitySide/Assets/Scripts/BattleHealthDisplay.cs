using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Text))]
public sealed class BattleHealthDisplay : MonoBehaviour
{
    [SerializeField] private PlayerController player;
    private Text label;

    private void Awake()
    {
        label = GetComponent<Text>();
        label.raycastTarget = false;
    }

    private void Update()
    {
        if (player == null || label == null) return;
        string playerHealth = "Player HP " + player.CurrentHealth.ToString("0") + " / " + player.MaxHealth.ToString("0");
        if (player.OpponentMaxHealth <= 0f)
        {
            label.text = playerHealth;
            return;
        }
        string enemyHealth = "Enemy HP " + player.OpponentHealth.ToString("0") + " / " + player.OpponentMaxHealth.ToString("0");
        string state = player.IsDead ? "LOSE  [R] Restart" : player.OpponentHealth <= 0f ? "WIN  [R] Restart" : "STAGE " + player.OpponentStage;
        label.text = playerHealth + "\n" + enemyHealth + "\n" + state;
    }
}
