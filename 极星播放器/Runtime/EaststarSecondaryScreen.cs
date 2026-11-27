using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

// Passive display: no video player, audio source, ownership, or network state.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class EaststarSecondaryScreen : UdonSharpBehaviour
{
    [InspectorName("主屏编号")]
    [Tooltip("填写主屏 EaststarScreen 的副屏编号。构建前自动配对，也可使用菜单 3 手动刷新。")]
    public int mainScreenId = 1;
    // Settings are read by the editor placement tool only; not network synced or used in Update.
    [InspectorName("吸附面")] public bool snapToSurface = false;
    [InspectorName("吸附距离（米）")] public float snapDistance = .35f;
    [InspectorName("离面间隙（米）")] public float snapOffset = .003f;
    [InspectorName("自动贴合朝向")] public bool snapAlignNormal = true;
    [HideInInspector] public EaststarScreen mainScreen;
    [HideInInspector] public GameObject standbySurface;
    [HideInInspector] public GameObject videoSurface;
    private bool initialized;
    private bool showing;

    private void Start() { RefreshDisplay(); }
    private void Update() { RefreshDisplay(); }

    private void RefreshDisplay()
    {
        bool next = false;
        if (mainScreenId > 0 && Utilities.IsValid(mainScreen) && mainScreen.secondaryScreenId == mainScreenId)
            next = mainScreen.IsShowingVideo();
        if (initialized && next == showing) return;
        initialized = true;
        showing = next;
        if (Utilities.IsValid(videoSurface)) videoSurface.SetActive(next);
        if (Utilities.IsValid(standbySurface)) standbySurface.SetActive(!next);
    }
}
