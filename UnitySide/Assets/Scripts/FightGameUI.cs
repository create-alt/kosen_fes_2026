using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class FightGameUI : MonoBehaviour
{
    [Serializable]
    public sealed class Hud
    {
        public GameObject root;
        public Image playerFill;
        public Image enemyFill;
        public Text playerValue;
        public Text enemyValue;
        public Text enemyName;
        public Text stage;
        public Text status;
        public Image[] phaseMarks;
    }

    [Serializable]
    public sealed class Views
    {
        public GameObject setup;
        public Button[] enemyCards;
        public Image[] enemyFaces;
        public GameObject[] selectedMarks;
        public Text chosenEnemy;
        public Button start;
        public Button[] difficultyButtons;
        public Image[] difficultyFaces;
        public Text difficultyHelp;
        public Toggle keyboard;
        public Slider volume;
        public Text volumeValue;
        public Text inputHelp;
        public InputField playerHealth;
        public InputField enemyHealth;
        public Button playerHealthMinus;
        public Button playerHealthPlus;
        public Button enemyHealthMinus;
        public Button enemyHealthPlus;
        public Hud[] huds;
        public GameObject rush;
        public Text rushCount;
        public Text rushTime;
        public GameObject result;
        public Text resultTitle;
        public Text resultDetail;
        public Button retry;
        public Button resultMenu;
        public Button battleMenu;
    }

    [SerializeField] private PlayerController player;
    [SerializeField] private EnemySelectionController selection;
    [SerializeField] private Views view;
    private int selectedEnemy;
    private int selectedDifficulty = 1;
    private static readonly Color Accent = new Color(0.16f, 0.88f, 0.78f);
    private static readonly Color Panel = new Color(0.075f, 0.11f, 0.17f);
    private static readonly Color EnemyColor = new Color(1f, 0.34f, 0.3f);

    public void Configure(PlayerController targetPlayer, EnemySelectionController selector, Views references)
    {
        player = targetPlayer;
        selection = selector;
        view = references;
    }

    private void Awake()
    {
        if (player == null || selection == null || view == null) return;
        FightSettings.Load();
        selectedDifficulty = FightSettings.Difficulty;
        view.keyboard.SetIsOnWithoutNotify(FightSettings.KeyboardTest);
        view.volume.SetValueWithoutNotify(FightSettings.Volume);
        if (view.playerHealth != null)
        {
            view.playerHealth.SetTextWithoutNotify(FightSettings.PlayerHealth.ToString());
            view.enemyHealth.SetTextWithoutNotify(FightSettings.EnemyHealth.ToString());
            view.playerHealth.onEndEdit.AddListener(_ => NormalizeHealth(view.playerHealth, FightSettings.PlayerHealth));
            view.enemyHealth.onEndEdit.AddListener(_ => NormalizeHealth(view.enemyHealth, FightSettings.EnemyHealth));
            view.playerHealthMinus.onClick.AddListener(() => AdjustHealth(view.playerHealth, -10));
            view.playerHealthPlus.onClick.AddListener(() => AdjustHealth(view.playerHealth, 10));
            view.enemyHealthMinus.onClick.AddListener(() => AdjustHealth(view.enemyHealth, -10));
            view.enemyHealthPlus.onClick.AddListener(() => AdjustHealth(view.enemyHealth, 10));
        }
        for (int index = 0; index < view.enemyCards.Length; index++)
        {
            int choice = index;
            view.enemyCards[index].onClick.AddListener(() => ChooseEnemy(choice));
        }
        for (int index = 0; index < view.difficultyButtons.Length; index++)
        {
            int choice = index;
            view.difficultyButtons[index].onClick.AddListener(() => ChooseDifficulty(choice));
        }
        view.start.onClick.AddListener(StartBattle);
        view.retry.onClick.AddListener(Retry);
        view.resultMenu.onClick.AddListener(ReturnToMenu);
        view.battleMenu.onClick.AddListener(ReturnToMenu);
        view.keyboard.onValueChanged.AddListener(_ => RefreshSettings());
        view.volume.onValueChanged.AddListener(UpdateVolume);
        ChooseEnemy(0);
        RefreshSettings();
        view.result.SetActive(false);
    }

    public void ChooseEnemy(int index)
    {
        if (selection == null || index < 0 || index >= selection.EnemyCount) return;
        selectedEnemy = index;
        for (int choice = 0; choice < view.enemyFaces.Length; choice++)
        {
            view.enemyFaces[choice].color = choice == index ? new Color(0.08f, 0.23f, 0.26f) : Panel;
            view.selectedMarks[choice].SetActive(choice == index);
        }
        view.chosenEnemy.text = "選択中：" + EnemyName(selection.GetEnemy(index));
    }

    public void ChooseDifficulty(int index)
    {
        selectedDifficulty = Mathf.Clamp(index, 0, 2);
        RefreshSettings();
    }

    private void RefreshSettings()
    {
        for (int index = 0; index < view.difficultyFaces.Length; index++) view.difficultyFaces[index].color = index == selectedDifficulty ? new Color(0.08f, 0.36f, 0.34f) : new Color(0.12f, 0.18f, 0.27f);
        view.difficultyHelp.text = selectedDifficulty == 0 ? "予告は長め。連射は1発ずつ。" : selectedDifficulty == 2 ? "速めのテンポ。連射が増えます。" : "敵HPに合わせて3段階で変化します。";
        view.volumeValue.text = Mathf.RoundToInt(view.volume.value * 100f) + "%";
        view.inputHelp.text = view.keyboard.isOn ? "Space：パンチ   W / S：前後移動\n↑ / ↓：高さ   J：しゃがむ   Home：姿勢リセット" : "画像認識からの動きで操作します。\nキーボードのテスト操作はOFFです。";
    }

    private void SaveSettings()
    {
        FightSettings.Set(selectedDifficulty, view.keyboard.isOn, view.volume.value);
        int playerHealth = ReadHealth(view.playerHealth, FightSettings.PlayerHealth);
        int enemyHealth = ReadHealth(view.enemyHealth, FightSettings.EnemyHealth);
        FightSettings.SetHealth(playerHealth, enemyHealth);
        NormalizeHealth(view.playerHealth, playerHealth);
        NormalizeHealth(view.enemyHealth, enemyHealth);
        FightSettings.Save();
        player.SetKeyboardTestEnabled(view.keyboard.isOn);
    }

    private static int ReadHealth(InputField field, int fallback)
    {
        return field != null && int.TryParse(field.text, out int value) ? FightSettings.ClampHealth(value) : fallback;
    }

    private static void NormalizeHealth(InputField field, int fallback)
    {
        if (field != null) field.SetTextWithoutNotify(ReadHealth(field, fallback).ToString());
    }

    private static void AdjustHealth(InputField field, int change)
    {
        if (field == null) return;
        field.SetTextWithoutNotify(FightSettings.ClampHealth(ReadHealth(field, 100) + change).ToString());
    }

    private void UpdateVolume(float value)
    {
        AudioListener.volume = value;
        RefreshSettings();
    }

    public void StartBattle()
    {
        if (!selection.IsSelecting) return;
        SaveSettings();
        player.SetMaxHealth(FightSettings.PlayerHealth);
        selection.SelectEnemy(selectedEnemy);
        if (UnityEngine.EventSystems.EventSystem.current != null) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        RefreshHuds(true);
    }

    public void Retry()
    {
        player.ResetBattle();
        if (UnityEngine.EventSystems.EventSystem.current != null) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        view.result.SetActive(false);
        RefreshHuds(true);
    }

    public void ReturnToMenu()
    {
        SaveSettings();
        selection.ReturnToSelection();
        view.result.SetActive(false);
        RefreshHuds(true);
    }

    private void Update()
    {
        if (player == null || selection == null || view == null) return;
        RefreshHuds(false);
        bool finished = !selection.IsSelecting && (player.IsDead || (player.OpponentMaxHealth > 0f && player.OpponentHealth <= 0f));
        if (view.rush != null)
        {
            view.rush.SetActive(!selection.IsSelecting && player.IsFinisherRush);
            view.rushCount.text = "連打！ " + player.RushHits + " / " + player.RushGoal;
            view.rushTime.text = player.RushRemaining.ToString("0.0") + " 秒";
        }
        if (finished && !view.result.activeSelf && (player.IsDead || !player.RushSucceeded)) BattleAudio.Play(player.IsDead ? FightSound.Defeat : FightSound.Victory);
        view.result.SetActive(finished);
        if (!finished) return;
        RefreshHuds(true);
        view.resultTitle.text = player.IsDead ? "DEFEAT" : player.RushSucceeded ? "RUSH K.O." : "VICTORY";
        view.resultTitle.color = player.IsDead ? EnemyColor : Accent;
        view.resultDetail.text = player.IsDead ? "もう一度、タイミングをつかもう。" : "相手を倒しました！";
    }

    private void RefreshHuds(bool immediate)
    {
        for (int hudIndex = 0; hudIndex < view.huds.Length; hudIndex++)
        {
            Hud hud = view.huds[hudIndex];
            hud.root.SetActive(!selection.IsSelecting && hudIndex == 0);
            if (selection.IsSelecting || hudIndex != 0) continue;
            float playerRatio = player.CurrentHealth / Mathf.Max(1f, player.MaxHealth);
            float enemyRatio = player.OpponentHealth / Mathf.Max(1f, player.OpponentMaxHealth);
            hud.playerFill.fillAmount = immediate ? playerRatio : Mathf.MoveTowards(hud.playerFill.fillAmount, playerRatio, Time.unscaledDeltaTime * 2f);
            hud.enemyFill.fillAmount = immediate ? enemyRatio : Mathf.MoveTowards(hud.enemyFill.fillAmount, enemyRatio, Time.unscaledDeltaTime * 2f);
            hud.playerFill.color = playerRatio > 0.3f ? Accent : new Color(1f, 0.65f, 0.15f);
            hud.playerValue.text = player.CurrentHealth.ToString("0") + " / " + player.MaxHealth.ToString("0");
            hud.enemyValue.text = player.OpponentHealth.ToString("0") + " / " + player.OpponentMaxHealth.ToString("0");
            hud.enemyName.text = EnemyName(selection.SelectedEnemy);
            hud.stage.text = "STAGE " + player.OpponentStage;
            hud.status.text = player.IsDead ? "敗北" : player.OpponentHealth <= 0f ? "勝利" : "";
            for (int index = 0; index < hud.phaseMarks.Length; index++) hud.phaseMarks[index].color = index < player.OpponentStage ? Accent : new Color(0.2f, 0.24f, 0.3f);
        }
    }

    private static string EnemyName(GameObject enemy)
    {
        if (enemy == null) return "ENEMY";
        return enemy.GetComponentInChildren<DilatantEnemy>(true) != null ? "DILATANT SENTINEL" : "PUNCHBOT";
    }
}
