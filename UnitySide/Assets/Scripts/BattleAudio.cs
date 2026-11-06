using System;
using System.Collections.Generic;
using UnityEngine;

public enum FightSound
{
    Gong, PlayerPunch, EnemyPunch, EnemyHit, PlayerHit, Missile, Liquid, Block,
    RushStart, RushHit, RushTick, RushSuccess, Victory, Defeat, Dodge
}

[DefaultExecutionOrder(-100)]
[DisallowMultipleComponent]
public sealed class BattleAudio : MonoBehaviour
{
    [Serializable]
    public sealed class SoundClip
    {
        public FightSound sound;
        public AudioClip clip;
    }

    [SerializeField] private PlayerController player;
    [SerializeField] private SoundClip[] clips;
    private static BattleAudio instance;
    private readonly Dictionary<FightSound, AudioClip> library = new Dictionary<FightSound, AudioClip>();
    private readonly AudioSource[] sources = new AudioSource[3];
    private float beatRemaining;
    public int PlayedCount { get; private set; }
    public FightSound LastSound { get; private set; }

    public void Configure(PlayerController owner, SoundClip[] sounds)
    {
        player = owner;
        clips = sounds;
        BuildLibrary();
    }

    private void Awake()
    {
        instance = this;
        BuildLibrary();
        for (int index = 0; index < sources.Length; index++)
        {
            GameObject channel = new GameObject("SoundChannel" + index);
            channel.transform.SetParent(transform, false);
            AudioSource source = channel.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.panStereo = index == 0 ? -0.7f : index == 2 ? 0.7f : 0f;
            source.volume = 0.75f;
            sources[index] = source;
        }
    }

    private void BuildLibrary()
    {
        library.Clear();
        if (clips != null)
        {
            foreach (SoundClip sound in clips) if (sound != null && sound.clip != null) library[sound.sound] = sound.clip;
        }
        if (!library.ContainsKey(FightSound.Dodge))
        {
            AudioClip dodge = Resources.Load<AudioClip>("FightAudio/Dodge");
            if (dodge != null) library[FightSound.Dodge] = dodge;
        }
    }

    public static void Play(FightSound sound, float side = 0f)
    {
        if (instance == null || !instance.library.TryGetValue(sound, out AudioClip clip)) return;
        int channel = side < -0.3f ? 0 : side > 0.3f ? 2 : 1;
        if (instance.sources[channel] == null) return;
        float gain = sound == FightSound.RushTick ? 0.2f : sound == FightSound.Gong ? 0.85f : 0.65f;
        instance.sources[channel].PlayOneShot(clip, gain);
        instance.PlayedCount++;
        instance.LastSound = sound;
    }

    public static void StopAll()
    {
        if (instance == null) return;
        foreach (AudioSource source in instance.sources) if (source != null) source.Stop();
    }

    private void Update()
    {
        if (player == null || !player.IsFinisherRush)
        {
            beatRemaining = 0f;
            return;
        }
        beatRemaining -= Time.deltaTime;
        if (beatRemaining > 0f) return;
        Play(FightSound.RushTick);
        beatRemaining = player.RushRemaining <= 2f ? 0.25f : 0.5f;
    }

    private void OnDisable()
    {
        if (instance == this) instance = null;
    }
}
