using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Video.Components.Base;
using VRC.SDK3.Components.Video;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class EaststarDurationProbe : UdonSharpBehaviour
{
    public EaststarScreen screen;
    public BaseVRCVideoPlayer player;
    private bool waiting;
    private float deadline;
    public void _Begin(VRCUrl url)
    {
        if (!Utilities.IsValid(player) || VRCUrl.IsNullOrEmpty(url)) { if (Utilities.IsValid(screen)) screen._ProbeResult(-1f); return; }
        waiting=true; deadline=Time.realtimeSinceStartup+20f;
        player.LoadURL(url);
    }
    public void _Cancel() { if (!waiting) return; waiting=false; if (Utilities.IsValid(player)) player.Stop(); }
    private void Update() { if (waiting && Time.realtimeSinceStartup>=deadline) Finish(-1f); }
    public override void OnVideoReady() { if (waiting) Finish(player.GetDuration()); }
    public override void OnVideoError(VideoError error) { if (waiting) Finish(-1f); }
    private void Finish(float value) { waiting=false; player.Stop(); if (Utilities.IsValid(screen)) screen._ProbeResult(value); }
}
