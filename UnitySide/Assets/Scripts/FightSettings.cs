using UnityEngine;

public static class FightSettings
{
    public static int Difficulty { get; private set; } = 1;
    public static bool KeyboardTest { get; private set; } = true;
    public static float Volume { get; private set; } = 0.8f;
    public const int MinimumHealth = 10;
    public const int MaximumHealth = 1000;
    public static int PlayerHealth { get; private set; } = 100;
    public static int EnemyHealth { get; private set; } = 100;

    public static int ClampHealth(int value) => Mathf.Clamp(value, MinimumHealth, MaximumHealth);

    public static void SetHealth(int playerHealth, int enemyHealth)
    {
        PlayerHealth = ClampHealth(playerHealth);
        EnemyHealth = ClampHealth(enemyHealth);
    }

    public static void Set(int difficulty, bool keyboardTest, float volume)
    {
        Difficulty = Mathf.Clamp(difficulty, 0, 2);
        KeyboardTest = keyboardTest;
        Volume = Mathf.Clamp01(volume);
        AudioListener.volume = Volume;
    }

    public static void Load()
    {
        Set(PlayerPrefs.GetInt("FightUI.Difficulty", 1), PlayerPrefs.GetInt("FightUI.Keyboard", 1) != 0, PlayerPrefs.GetFloat("FightUI.Volume", 0.8f));
        SetHealth(PlayerPrefs.GetInt("FightUI.PlayerHealth", 100), PlayerPrefs.GetInt("FightUI.EnemyHealth", 100));
    }

    public static void Save()
    {
        PlayerPrefs.SetInt("FightUI.Difficulty", Difficulty);
        PlayerPrefs.SetInt("FightUI.Keyboard", KeyboardTest ? 1 : 0);
        PlayerPrefs.SetFloat("FightUI.Volume", Volume);
        PlayerPrefs.SetInt("FightUI.PlayerHealth", PlayerHealth);
        PlayerPrefs.SetInt("FightUI.EnemyHealth", EnemyHealth);
        PlayerPrefs.Save();
    }
}
