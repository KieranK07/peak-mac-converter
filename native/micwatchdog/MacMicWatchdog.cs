// MacMicWatchdog (ours, Mac only): restarts Photon Voice recording when Unity's microphone stops delivering samples.
//
// Why: some macOS inputs change format right after capture starts. AirPods go 24 kHz -> 48 kHz and their IO buffer
// 240 -> 480 frames about 0.3 s in. Unity 6000.3's FMOD recorder never adapts: every CoreAudio cycle then fails
// ("expected 1920-byte buffer; mDataByteSize=960", -50), Microphone.IsRecording stays true and GetPosition
// freezes, so Photon's MicWrapper reads nothing and peers hear nothing. Microphone.End + Start fixes it, which is
// what Recorder.RestartRecording() does.
//
// Inert while samples arrive: every 0.5 s it only reads Microphone.devices/IsRecording/GetPosition. It acts only
// when a recording device's position has moved and then stays unchanged for >= 1 s. Windows never stalls like
// this, so Windows behaviour is kept wherever Windows works.
// Loaded through RuntimeInitializeOnLoads.json (scripts/assemble_app.py adds the entry); no game DLL is changed.
using System.Collections.Generic;
using Photon.Voice.Unity;
using UnityEngine;

public class MacMicWatchdog : MonoBehaviour
{
    const float Poll = 0.5f, Stall = 1f, Cooldown = 5f;
    readonly Dictionary<string, int> lastPos = new Dictionary<string, int>();
    readonly Dictionary<string, float> stalledFor = new Dictionary<string, float>();
    readonly HashSet<string> moved = new HashSet<string>();
    float nextPoll, lastRestart = -100f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (Application.platform != RuntimePlatform.OSXPlayer) return;
        var go = new GameObject("[MacMicWatchdog]") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(go);
        go.AddComponent<MacMicWatchdog>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextPoll) return;
        nextPoll = Time.unscaledTime + Poll;
        foreach (var dev in Microphone.devices)
        {
            // ponytail: a device that never delivers a first sample (e.g. TCC prompt still open) is left alone
            if (!Microphone.IsRecording(dev)) { lastPos.Remove(dev); moved.Remove(dev); stalledFor.Remove(dev); continue; }
            int pos = Microphone.GetPosition(dev);
            bool same = lastPos.TryGetValue(dev, out int last) && last == pos;
            if (lastPos.ContainsKey(dev) && !same) moved.Add(dev);
            lastPos[dev] = pos;
            stalledFor[dev] = same && moved.Contains(dev) ? stalledFor.GetValueOrDefault(dev) + Poll : 0f;
            if (stalledFor[dev] < Stall || Time.unscaledTime - lastRestart < Cooldown) continue;
            int n = 0;
            foreach (var r in FindObjectsByType<Recorder>(FindObjectsSortMode.None))
                if (r.isActiveAndEnabled && r.RecordingEnabled && r.SourceType == Recorder.InputSourceType.Microphone
                    && r.MicrophoneType == Recorder.MicType.Unity && r.RestartRecording()) n++;
            Debug.Log($"[MacMicWatchdog] microphone '{dev}' stopped delivering samples (position {pos} for {stalledFor[dev]:F1} s); restarting {n} recorder(s)");
            lastRestart = Time.unscaledTime;
            lastPos.Remove(dev); moved.Remove(dev); stalledFor.Remove(dev);
        }
    }
}
