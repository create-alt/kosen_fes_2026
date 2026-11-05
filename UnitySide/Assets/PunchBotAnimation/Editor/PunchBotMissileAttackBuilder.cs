using System;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class PunchBotMissileAttackBuilder
{
    private const string Folder = "Assets/PunchBotGenerated";
    private const float FirstShotTime = 0.35f;
    private const float ShotInterval = 0.35f;
    private const float RecoveryTime = 0.55f;

    [MenuItem("Tools/PunchBot/Create Missile Attack Animations")]
    public static void Build()
    {
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(Folder + "/PunchBot.controller");
        if (controller == null) throw new FileNotFoundException("PunchBot.controller was not found in " + Folder);

        AnimatorStateMachine machine = controller.layers[0].stateMachine;
        AnimatorState idle = FindState(machine, "Idle");
        if (idle == null) throw new InvalidOperationException("The Idle state was not found.");

        for (int count = 1; count <= 3; count++)
        {
            string name = "MissileAttack" + count;
            string path = Folder + "/" + name + ".anim";
            AnimationClip created = CreateClip(name, count);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);

            if (clip == null)
            {
                AssetDatabase.CreateAsset(created, path);
                clip = created;
            }
            else
            {
                EditorUtility.CopySerialized(created, clip);
                UnityEngine.Object.DestroyImmediate(created);
                EditorUtility.SetDirty(clip);
            }

            AnimatorState state = FindState(machine, name);
            if (state == null) state = machine.AddState(name, new Vector3(520f, count * 90f, 0f));
            state.motion = clip;
            state.writeDefaultValues = false;

            bool hasRecovery = false;
            foreach (AnimatorStateTransition transition in state.transitions)
            {
                if (transition.destinationState == idle) hasRecovery = true;
            }

            if (!hasRecovery)
            {
                AnimatorStateTransition recover = state.AddTransition(idle);
                recover.hasExitTime = true;
                recover.exitTime = 1f;
                recover.hasFixedDuration = true;
                recover.duration = 0f;
            }

            EditorUtility.SetDirty(state);
        }

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log("Created PunchBot missile attacks: 1, 2 and 3 shots.");
    }

    [MenuItem("Tools/PunchBot/Create Punch Attack Animation")]
    public static void BuildPunch()
    {
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(Folder + "/PunchBot.controller");
        if (controller == null) throw new FileNotFoundException("PunchBot.controller was not found in " + Folder);
        AnimatorStateMachine machine = controller.layers[0].stateMachine;
        AnimatorState idle = FindState(machine, "Idle");
        if (idle == null) throw new InvalidOperationException("The Idle state was not found.");

        string[] names = { "PunchAttack", "JabAttack", "BoxingGuard" };
        for (int index = 0; index < names.Length; index++)
        {
            string name = names[index];
            bool guardOnly = index == 2;
            string path = Folder + "/" + name + ".anim";
            AnimationClip created = CreateClip(name, 1, true, index == 1, guardOnly);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                AssetDatabase.CreateAsset(created, path);
                clip = created;
            }
            else
            {
                EditorUtility.CopySerialized(created, clip);
                UnityEngine.Object.DestroyImmediate(created);
                EditorUtility.SetDirty(clip);
            }

            AnimatorState state = FindState(machine, name);
            if (state == null) state = machine.AddState(name, new Vector3(780f, 90f + index * 90f, 0f));
            state.motion = clip;
            state.writeDefaultValues = false;
            bool hasRecovery = false;
            foreach (AnimatorStateTransition transition in state.transitions)
            {
                if (transition.destinationState == idle) hasRecovery = true;
            }

            if (!guardOnly && !hasRecovery)
            {
                AnimatorStateTransition recover = state.AddTransition(idle);
                recover.hasExitTime = true;
                recover.exitTime = 1f;
                recover.hasFixedDuration = true;
                recover.duration = 0f;
            }

            EditorUtility.SetDirty(state);
        }

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log("Created PunchBot boxing guard, left jab (0.14s impact) and right straight (0.20s impact).");
    }

    private static AnimationClip CreateClip(string name, int count, bool punch = false, bool jab = false, bool guardOnly = false)
    {
        AnimationClip clip = new AnimationClip { name = name, frameRate = 60f };
        float lastShotTime = FirstShotTime + (count - 1) * ShotInterval;
        float duration = punch ? (guardOnly ? 0.8f : (jab ? 0.42f : 0.55f)) : lastShotTime + RecoveryTime;
        float impactTime = punch ? (jab ? 0.14f : 0.2f) : FirstShotTime;
        string[] paths =
        {
            "Visual", "Visual/Body", "Visual/LegL", "Visual/LegR", "Visual/Body/Head",
            "Visual/Body/ArmL", "Visual/Body/ArmR", "Visual/Body/ArmL/Elbow", "Visual/Body/ArmR/Elbow"
        };
        Vector3[] positions =
        {
            Vector3.zero, new Vector3(0f, 0.51f, 0f), new Vector3(-0.21f, 0.48f, 0f), new Vector3(0.21f, 0.48f, 0f),
            new Vector3(0f, 0.535f, 0f), new Vector3(-0.405f, 0.38f, 0f), new Vector3(0.405f, 0.38f, 0f),
            new Vector3(-0.085f, -0.255f, 0f), new Vector3(0.085f, -0.255f, 0f)
        };

        int frameCount = Mathf.RoundToInt(duration * clip.frameRate);
        for (int node = 0; node < paths.Length; node++)
        {
            AnimationCurve[] positionCurves = { new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
            AnimationCurve[] rotationCurves = { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };

            for (int frame = 0; frame <= frameCount; frame++)
            {
                float time = duration * frame / frameCount;
                float ready = Mathf.SmoothStep(0f, 1f, time / FirstShotTime);
                float recover = 1f - Mathf.SmoothStep(0f, 1f, (time - lastShotTime - 0.15f) / (RecoveryTime - 0.15f));
                float pose = ready * recover;
                float recoil = 0f;

                for (int shot = 0; shot < count; shot++)
                {
                    float sinceShot = time - (FirstShotTime + shot * ShotInterval);
                    if (sinceShot >= 0f && sinceShot <= 0.24f) recoil += Mathf.Sin(sinceShot / 0.24f * Mathf.PI);
                }

                Vector3 position = positions[node];
                Vector3 euler = Vector3.zero;
                switch (node)
                {
                    case 0:
                        position.y -= 0.04f * pose + 0.025f * recoil;
                        break;
                    case 1:
                        euler.x = -8f * pose - 7f * recoil;
                        break;
                    case 2:
                        euler.z = -5f * pose;
                        break;
                    case 3:
                        euler.z = 5f * pose;
                        break;
                    case 4:
                        euler.x = -18f * pose + 6f * recoil;
                        break;
                    case 5:
                        euler = new Vector3(-65f * pose - 12f * recoil, 0f, -28f * pose);
                        break;
                    case 6:
                        euler = new Vector3(-65f * pose - 12f * recoil, 0f, 28f * pose);
                        break;
                    case 7:
                    case 8:
                        euler.x = -45f * pose - 10f * recoil;
                        break;
                }

                if (punch)
                {
                    position = positions[node];
                    euler = Vector3.zero;
                    float guard = 1f;
                    float windupTime = jab ? 0.06f : 0.08f;
                    float extension = guardOnly ? 0f : Mathf.SmoothStep(0f, 1f, (time - windupTime) / (impactTime - windupTime)) * (1f - Mathf.SmoothStep(0f, 1f, (time - impactTime) / (jab ? 0.16f : 0.22f)));
                    switch (node)
                    {
                        case 0:
                            position.z += 0.04f * extension;
                            if (guardOnly) position.y += 0.004f * Mathf.Sin(time * Mathf.PI * 2f / duration);
                            break;
                        case 1:
                            euler = new Vector3(6f * extension, (jab ? 8f : -14f) * extension, 0f);
                            break;
                        case 2:
                            euler.z = -3f * guard;
                            break;
                        case 3:
                            euler.z = 3f * guard;
                            break;
                        case 4:
                            euler.y = (jab ? -8f : 14f) * extension;
                            break;
                        case 5:
                            euler = new Vector3(-55f * guard - (jab ? 35f * extension : 0f), 0f, -10f * guard);
                            if (jab) position.z += 0.12f * extension;
                            break;
                        case 6:
                            euler = new Vector3(-55f * guard - (jab ? 0f : 35f * extension), 0f, 10f * guard);
                            if (!jab) position.z += 0.16f * extension;
                            break;
                        case 7:
                            euler.x = -65f * guard + (jab ? 65f * extension : 0f);
                            break;
                        case 8:
                            euler.x = -65f * guard + (jab ? 0f : 65f * extension);
                            break;
                    }
                }

                Quaternion rotation = Quaternion.Euler(euler);
                for (int axis = 0; axis < 3; axis++) positionCurves[axis].AddKey(new Keyframe(time, position[axis]));
                for (int axis = 0; axis < 4; axis++) rotationCurves[axis].AddKey(new Keyframe(time, rotation[axis]));
            }

            string[] axes = { "x", "y", "z", "w" };
            for (int axis = 0; axis < 3; axis++) SetCurve(clip, paths[node], "m_LocalPosition." + axes[axis], positionCurves[axis]);
            for (int axis = 0; axis < 4; axis++) SetCurve(clip, paths[node], "m_LocalRotation." + axes[axis], rotationCurves[axis]);
        }

        AnimationEvent[] events = new AnimationEvent[guardOnly ? 0 : count + 1];
        for (int shot = 0; shot < (guardOnly ? 0 : count); shot++)
        {
            events[shot] = new AnimationEvent { time = impactTime + shot * ShotInterval, functionName = punch ? "OnPunchImpact" : "OnMissileLaunch" };
        }

        if (!guardOnly) events[count] = new AnimationEvent { time = duration - 1f / clip.frameRate, functionName = "OnMissileAttackFinished" };
        AnimationUtility.SetAnimationEvents(clip, events);
        clip.EnsureQuaternionContinuity();
        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = guardOnly;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        return clip;
    }

    private static void SetCurve(AnimationClip clip, string path, string property, AnimationCurve curve)
    {
        for (int key = 0; key < curve.length; key++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, key, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, key, AnimationUtility.TangentMode.Linear);
        }

        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), property), curve);
    }

    private static AnimatorState FindState(AnimatorStateMachine machine, string name)
    {
        foreach (ChildAnimatorState child in machine.states)
        {
            if (child.state.name == name) return child.state;
        }

        return null;
    }
}
