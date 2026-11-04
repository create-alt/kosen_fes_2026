using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

[DefaultExecutionOrder(-500)]
public sealed class EnemySelectionController : MonoBehaviour
{
    [Serializable]
    public sealed class EnemyChoice
    {
        public GameObject enemy;
        public Button selectButton;
    }

    [SerializeField] private PlayerController player;
    [SerializeField] private GameObject selectionPanel;
    [SerializeField] private EnemyChoice[] enemyChoices;
    [SerializeField] private bool requireStartButton;

    private UnityAction[] selectionActions;
    private bool configured;

    public bool IsSelecting { get; private set; }
    public GameObject SelectedEnemy { get; private set; }
    public int EnemyCount => enemyChoices == null ? 0 : enemyChoices.Length;
    public GameObject GetEnemy(int index) => index >= 0 && index < EnemyCount ? enemyChoices[index].enemy : null;

    private void Awake()
    {
        if (player == null || selectionPanel == null || enemyChoices == null || enemyChoices.Length == 0)
        {
            Debug.LogError("Player、Selection Panel、Enemy Choicesを設定してください。", this);
            return;
        }

        if (transform.IsChildOf(selectionPanel.transform) || player.transform.IsChildOf(selectionPanel.transform))
        {
            Debug.LogError("GameManagerとPlayerはSelection Panelの外に配置してください。", this);
            return;
        }

        for (int index = 0; index < enemyChoices.Length; index++)
        {
            EnemyChoice choice = enemyChoices[index];
            if (choice == null || choice.enemy == null || choice.selectButton == null)
            {
                Debug.LogError("各Enemy Choiceに敵のルートと選択ボタンを設定してください。", this);
                return;
            }

            if (player.transform.IsChildOf(choice.enemy.transform) || selectionPanel.transform.IsChildOf(choice.enemy.transform) || transform.IsChildOf(choice.enemy.transform) || choice.enemy.transform.IsChildOf(selectionPanel.transform))
            {
                Debug.LogError("EnemyにはPlayer、選択画面、GameManagerを含まない敵専用のルートを設定してください。", this);
                return;
            }

            for (int previous = 0; previous < index; previous++)
            {
                if (enemyChoices[previous].enemy == choice.enemy || enemyChoices[previous].selectButton == choice.selectButton)
                {
                    Debug.LogError("Enemy Choicesの敵と選択ボタンは別々のものを設定してください。", this);
                    return;
                }
            }
        }

        configured = true;
        selectionActions = new UnityAction[enemyChoices.Length];
        for (int index = 0; index < enemyChoices.Length; index++)
        {
            int selectedIndex = index;
            selectionActions[index] = () => SelectEnemy(selectedIndex);
        }

        BattleAudio.StopAll();
        player.SetGameplayEnabled(false);
        foreach (EnemyChoice choice in enemyChoices) choice.enemy.SetActive(false);
        SelectedEnemy = null;
        IsSelecting = true;
        selectionPanel.SetActive(true);
    }

    private void OnEnable()
    {
        if (!configured || requireStartButton) return;
        for (int index = 0; index < enemyChoices.Length; index++) enemyChoices[index].selectButton.onClick.AddListener(selectionActions[index]);
    }

    private void OnDisable()
    {
        if (!configured) return;
        for (int index = 0; index < enemyChoices.Length; index++)
        {
            if (enemyChoices[index].selectButton != null) enemyChoices[index].selectButton.onClick.RemoveListener(selectionActions[index]);
        }
    }

    public void SelectEnemy(int index)
    {
        if (!configured || !isActiveAndEnabled || !IsSelecting || index < 0 || index >= enemyChoices.Length) return;
        GameObject enemy = enemyChoices[index].enemy;
        if (enemy == null) return;

        SelectedEnemy = enemy;
        IsSelecting = false;
        enemy.SetActive(true);
        Animator animator = enemy.GetComponentInChildren<Animator>();
        if (animator != null) animator.Rebind();
        PunchBotMotion motion = enemy.GetComponentInChildren<PunchBotMotion>();
        DilatantEnemy liquid = enemy.GetComponentInChildren<DilatantEnemy>();
        if (motion != null) motion.SetMaxHealth(FightSettings.EnemyHealth);
        if (liquid != null) liquid.SetMaxHealth(FightSettings.EnemyHealth);
        if (animator != null) animator.Update(0f);
        player.SetOpponent(enemy.transform);
        player.SetGameplayEnabled(true);
        BattleAudio.StopAll();
        BattleAudio.Play(FightSound.Gong);
        selectionPanel.SetActive(false);
    }

    public void ReturnToSelection()
    {
        if (!configured) return;
        BattleAudio.StopAll();
        player.SetGameplayEnabled(false);
        foreach (EnemyChoice choice in enemyChoices) choice.enemy.SetActive(false);
        SelectedEnemy = null;
        IsSelecting = true;
        player.SetOpponent(null);
        selectionPanel.SetActive(true);
    }
}
