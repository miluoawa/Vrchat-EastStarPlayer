using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.Udon.Common;
using VRC.SDKBase;
using VRC.SDK3.Video.Components.Base;
using VRC.SDK3.Video.Components;
using VRC.SDK3.Components.Video;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class EaststarScreen : UdonSharpBehaviour
{
    [InspectorName("副屏编号")]
    [Tooltip("与副屏的主屏编号相同即可配对。每个主屏使用不同的正整数编号。")]
    public int secondaryScreenId = 1;
    public BaseVRCVideoPlayer videoPlayer;
    public GameObject standbySurface;
    public GameObject videoSurface;
    public AudioSource audioSource;
    [UdonSynced] private VRCUrl sharedUrl = VRCUrl.Empty;
    [UdonSynced] private int revision;
    [UdonSynced] private bool active;
    [UdonSynced] private double startTime;
    [UdonSynced] private bool clockStarted;
    [UdonSynced] private int initiatorId = -1;
    [UdonSynced] private int seekRevision;
    public EaststarDurationProbe durationProbe;
    [UdonSynced] private int queueCount;
    [UdonSynced] private VRCUrl[] queueUrls = new VRCUrl[] { VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty, VRCUrl.Empty };
    [UdonSynced] private string[] queueNames = new string[] { "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "" };
    [UdonSynced] private int[] queuePlayers = new int[20];
    [UdonSynced] private int[] queueTickets = new int[20];
    [UdonSynced] private float[] queueDurations = new float[20];
    [UdonSynced] private int nextTicket;
    [UdonSynced] private string currentName = "";
    [UdonSynced] private float currentDuration;
    private float nextSubmit;
    private int probeTicket = -1;
    private int appliedRevision = -1;
    private int appliedSeekRevision;
    private float lastLoad = -100f;
    private bool pending, loading, ready, playing;
    private float loadStarted, nextSync;
    private string status = "待机中";


    // Local personal viewing only. These references and all state below are NOT UdonSynced.
    [HideInInspector] public EaststarScreen[] personalSources;
    // Keep the existing personalVR field names for scene/prefab upgrade compatibility.
    // Both PC and VR now use the same local world-space pickup.
    [HideInInspector] public GameObject personalVR;
    [HideInInspector] public Renderer personalVRImage;
    [HideInInspector] public VRCPickup personalPickup;
    [HideInInspector] public Rigidbody personalBody;
    private EaststarScreen personalSource;
    private Material personalShownMaterial;
    private bool personalOpen, personalInVR, personalAudioChanged, personalVRCloned;
    private float personalOriginalBlend;
    private AudioSource personalAudio;
    private int leftTaps, rightTaps;
    private float leftFirst, rightFirst, leftLast, rightLast;
    private float lastPersonalToggle = -10f;

    // Local state only: never serialized, never sent as a network event.
    [HideInInspector] public CanvasGroup mainPlaybackIcon;
    [HideInInspector] public RawImage mainPlaybackImage;
    [HideInInspector] public Texture2D playIconTexture, pauseIconTexture;
    private CanvasGroup personalPlaybackIcon;
    private RawImage personalPlaybackImage;
    private bool localPaused;
    private float localPausedAt;
    private float lastLocalToggle = -100f;
    private float mainIconUntil = -100f, personalIconUntil = -100f;
    private int personalIconRevision = -1;
    [HideInInspector] public VRCUrl personalDefaultAddress = VRCUrl.Empty;
    private GameObject personalGear, personalPanel, personalReveal;
    private CanvasGroup personalGearGroup;
    private VRCUrlInputField personalInput;
    private Text personalStatus, personalQueue, personalTime, personalPauseText, personalPageText;
    private Button personalPauseButton, personalSkipButton, personalStopButton, personalPrevButton, personalNextButton;
    private Slider personalVolume, personalProgress;
    private bool personalSettingsOpen, personalUpdating, personalSeekPending;
    private int personalQueuePage, personalSeekRevision;
    private float personalRefreshAt, personalSeekAt, personalSeekFraction, personalMessageUntil;
    private float personalGearUntil = -100f;
    private string personalMessage = "";
    public float GetLocalVolume() { return Utilities.IsValid(audioSource) ? audioSource.volume : 0f; }

    public bool IsLocalPaused() { return localPaused; }
    public bool CanLocalPause()
    {
        // A finite duration is required so the queue owner can advance even while paused.
        return active && playing && ready && clockStarted && IsShowingVideo() && GetDuration() > 0f;
    }
    public float GetLocalPosition() { return localPaused ? localPausedAt : GetPosition(); }
    public float GetLastLocalToggle() { return lastLocalToggle; }
    public void ToggleLocalPause()
    {
        if (!CanLocalPause()) return;
        if (localPaused)
        {
            localPaused = false;
            videoPlayer.Play();
            SyncTime(true);
            nextSync = Time.realtimeSinceStartup + 2f;
            status = "正在播放";
        }
        else
        {
            localPausedAt = Mathf.Clamp(videoPlayer.GetTime(), 0f, GetDuration());
            localPaused = true;
            videoPlayer.Pause();
            status = "本地已暂停";
        }
        lastLocalToggle = Time.realtimeSinceStartup;
    }
    private void ResetLocalPause()
    {
        localPaused = false;
        localPausedAt = 0f;
        lastLocalToggle = -100f;
        mainIconUntil = -100f;
    }
    public void RevealMainPlaybackIcon()
    {
        if (CanLocalPause()) mainIconUntil = Time.realtimeSinceStartup + 3f;
    }
    public void ClickMainPlaybackIcon() { ToggleLocalPause(); }
    public void RevealPersonalPlaybackIcon()
    {
        if (!personalOpen || personalSettingsOpen || !Utilities.IsValid(personalSource)) return;
        if (Utilities.IsValid(personalGear))
        {
            personalGear.SetActive(true);
            personalGearUntil = Time.realtimeSinceStartup + 3f;
            if (Utilities.IsValid(personalGearGroup))
            {
                personalGearGroup.alpha = 1f;
                personalGearGroup.interactable = true;
                personalGearGroup.blocksRaycasts = true;
            }
        }
        if (personalSource.CanLocalPause()) personalIconUntil = Time.realtimeSinceStartup + 3f;
    }
    public void ClickPersonalPlaybackIcon()
    {
        if (personalOpen && !personalSettingsOpen && Utilities.IsValid(personalSource)) personalSource.ToggleLocalPause();
    }
    private void UpdatePersonalGear()
    {
        if (!Utilities.IsValid(personalGear) || personalSettingsOpen) return;
        float alpha = 0f;
        if (personalGearUntil > -50f)
            alpha = Mathf.Clamp01(1f - (Time.realtimeSinceStartup - personalGearUntil) / .5f);
        bool visible = alpha > 0f;
        if (personalGear.activeSelf != visible) personalGear.SetActive(visible);
        if (!Utilities.IsValid(personalGearGroup) || !visible)
        {
            if (Utilities.IsValid(personalGearGroup))
            {
                personalGearGroup.alpha = 0f;
                personalGearGroup.interactable = false;
                personalGearGroup.blocksRaycasts = false;
            }
            return;
        }
        personalGearGroup.alpha = alpha;
        personalGearGroup.interactable = true;
        personalGearGroup.blocksRaycasts = true;
    }
    private void UpdatePlaybackIcon(CanvasGroup group, RawImage image, EaststarScreen source, float revealUntil)
    {
        if (!Utilities.IsValid(group) || !Utilities.IsValid(image)) return;
        float alpha = 0f;
        bool paused = false;
        if (Utilities.IsValid(source) && source.CanLocalPause())
        {
            paused = source.IsLocalPaused();
            float until = Mathf.Max(revealUntil, source.GetLastLocalToggle() + 3f);
            alpha = paused ? 1f : Mathf.Clamp01(1f - (Time.realtimeSinceStartup - until) / .5f);
        }
        bool visible = alpha > 0f;
        if (group.gameObject.activeSelf != visible) group.gameObject.SetActive(visible);
        if (!visible) return;
        group.alpha = alpha;
        // Requested UI behavior: when paused, show the play icon; when playing, show the pause icon.
        Texture2D icon = paused ? playIconTexture : pauseIconTexture;
        if (Utilities.IsValid(icon) && image.texture != icon) image.texture = icon;
    }

    private void Start() { ApplyState(); }
    public int GetQueueCount() { return queueCount; }
    public string GetQueueAddress(int index) { return index >= 0 && index < queueCount && !VRCUrl.IsNullOrEmpty(queueUrls[index]) ? queueUrls[index].Get() : ""; }
    public string GetQueueName(int index) { return index >= 0 && index < queueCount ? queueNames[index] : ""; }
    public float GetQueueDuration(int index) { return index >= 0 && index < queueCount ? queueDurations[index] : 0f; }
    public string GetCurrentName() { return currentName; }
    public string GetCurrentAddress() { return active && !VRCUrl.IsNullOrEmpty(sharedUrl) ? sharedUrl.Get() : ""; }
    public bool IsActive() { return active; }
    public bool IsShowingVideo()
    {
        return active && playing && Utilities.IsValid(videoSurface) && videoSurface.activeInHierarchy;
    }
    public string GetStatus() { return status; }
    public int GetRevision() { return revision; }
    public bool CanLoad() { return Time.realtimeSinceStartup - lastLoad >= 5.2f; }
    public void SetLocalVolume(float value) { if (Utilities.IsValid(audioSource)) audioSource.volume = Mathf.Clamp01(value); }
    public bool IsInitiator()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        return active && Utilities.IsValid(local) && local.playerId == initiatorId;
    }
    public string GetControllerText()
    {
        if (!active) return "暂无播放者";
        if (IsInitiator()) return "你是播放者，可以调整进度";
        VRCPlayerApi player = VRCPlayerApi.GetPlayerById(initiatorId);
        if (!Utilities.IsValid(player)) return "播放者已离开，进度已锁定";
        return "播放者：" + player.displayName + "（仅播放者可调整进度）";
    }
    public float GetDuration()
    {
        if (!active) return 0f;
        if (!ready || !Utilities.IsValid(videoPlayer)) return currentDuration;
        float duration = videoPlayer.GetDuration();
        return duration > 0f && !float.IsInfinity(duration) && !float.IsNaN(duration) ? duration : 0f;
    }
    public float GetPosition()
    {
        float duration = GetDuration();
        if (duration <= 0f || !clockStarted) return 0f;
        return Mathf.Clamp((float)(Networking.GetServerTimeInSeconds() - startTime), 0f, duration);
    }
    public bool CanSeek() { return IsInitiator() && playing && !localPaused && clockStarted && GetDuration() > 0f; }
    public bool Seek(float fraction, int expectedRevision)
    {
        if (!CanSeek() || expectedRevision != revision || float.IsNaN(fraction)) return false;
        if (Networking.IsOwner(gameObject)) ApplySeek(fraction, expectedRevision, Networking.LocalPlayer);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReceiveSeek), fraction, expectedRevision);
        return true;
    }
    [NetworkCallable(maxEventsPerSecond: 3)]
    public void ReceiveSeek(float fraction, int expectedRevision)
    { ApplySeek(fraction, expectedRevision, NetworkCalling.CallingPlayer); }
    private void ApplySeek(float fraction, int expectedRevision, VRCPlayerApi caller)
    {
        if (!Networking.IsOwner(gameObject) || !Utilities.IsValid(caller) || caller.playerId != initiatorId || !active || !playing || !clockStarted || expectedRevision != revision || float.IsNaN(fraction)) return;
        float duration = GetDuration(); if (duration <= 0f) return;
        float target = Mathf.Clamp(Mathf.Clamp01(fraction) * duration, 0f, Mathf.Max(0f, duration - .25f));
        startTime = Networking.GetServerTimeInSeconds() - target;
        seekRevision++; appliedSeekRevision = seekRevision;
        if (!localPaused) videoPlayer.SetTime(target);
        RequestSerialization();
        nextSync = Time.realtimeSinceStartup + 2f;
    }
    public bool Submit(VRCUrl url)
    {
        if (!Networking.IsNetworkSettled || !Utilities.IsValid(Networking.LocalPlayer) || !Utilities.IsValid(videoPlayer) || VRCUrl.IsNullOrEmpty(url) || queueCount >= queueUrls.Length || Time.realtimeSinceStartup < nextSubmit) return false;
        nextSubmit = Time.realtimeSinceStartup + 1f;
        if (Networking.IsOwner(gameObject)) AddToQueue(url, Networking.LocalPlayer);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReceiveQueueAdd), url);
        return true;
    }
    [NetworkCallable(maxEventsPerSecond: 2)]
    public void ReceiveQueueAdd(VRCUrl url) { AddToQueue(url, NetworkCalling.CallingPlayer); }
    private void AddToQueue(VRCUrl url, VRCPlayerApi caller)
    {
        if (!Networking.IsOwner(gameObject) || !Utilities.IsValid(caller) || VRCUrl.IsNullOrEmpty(url) || queueCount >= queueUrls.Length) return;
        string address = url.Get();
        if (address.Length > 2048 || (!address.StartsWith("https://") && !address.StartsWith("http://"))) return;
        int i = queueCount++;
        queueUrls[i] = url; queueNames[i] = caller.displayName; queuePlayers[i] = caller.playerId;
        queueTickets[i] = ++nextTicket; queueDurations[i] = 0f;
        if (!active || (!playing && !loading && !ready && !pending)) AdvanceQueue(); else RequestSerialization();
    }
    public bool CanManageQueue() { return Networking.IsMaster; }
    public void _SkipForEveryone()
    {
        if (!Networking.IsMaster) return;
        if (Networking.IsOwner(gameObject)) ApplySkip(revision, Networking.LocalPlayer);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReceiveSkip), revision);
    }
    [NetworkCallable(maxEventsPerSecond: 2)]
    public void ReceiveSkip(int expectedRevision) { ApplySkip(expectedRevision, NetworkCalling.CallingPlayer); }
    private void ApplySkip(int expectedRevision, VRCPlayerApi caller)
    {
        if (!Networking.IsOwner(gameObject) || !Utilities.IsValid(caller) || !caller.isMaster || expectedRevision != revision) return;
        AdvanceQueue();
    }
    public void _StopForEveryone()
    {
        if (!Networking.IsMaster) return;
        if (Networking.IsOwner(gameObject)) ApplyStop(Networking.LocalPlayer);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReceiveStop));
    }
    [NetworkCallable(maxEventsPerSecond: 1)]
    public void ReceiveStop() { ApplyStop(NetworkCalling.CallingPlayer); }
    private void ApplyStop(VRCPlayerApi caller)
    {
        if (!Networking.IsOwner(gameObject) || !Utilities.IsValid(caller) || !caller.isMaster) return;
        for (int i=0;i<queueCount;i++) { queueUrls[i]=VRCUrl.Empty; queueNames[i]=""; queuePlayers[i]=0; queueTickets[i]=0; queueDurations[i]=0f; }
        queueCount=0; AdvanceQueue();
    }
    private void AdvanceQueue()
    {
        if (!Networking.IsOwner(gameObject)) return;
        CancelProbe();
        revision++; seekRevision=0; clockStarted=false; startTime=Networking.GetServerTimeInSeconds();
        if (queueCount <= 0)
        {
            active=false; sharedUrl=VRCUrl.Empty; initiatorId=-1; currentName=""; currentDuration=0f;
        }
        else
        {
            active=true; sharedUrl=queueUrls[0]; initiatorId=queuePlayers[0]; currentName=queueNames[0]; currentDuration=Mathf.Max(0f,queueDurations[0]);
            for (int i=1;i<queueCount;i++) { queueUrls[i-1]=queueUrls[i]; queueNames[i-1]=queueNames[i]; queuePlayers[i-1]=queuePlayers[i]; queueTickets[i-1]=queueTickets[i]; queueDurations[i-1]=queueDurations[i]; }
            queueCount--; queueUrls[queueCount]=VRCUrl.Empty; queueNames[queueCount]=""; queuePlayers[queueCount]=0; queueTickets[queueCount]=0; queueDurations[queueCount]=0f;
        }
        RequestSerialization(); ApplyState();
    }
    private void CancelProbe()
    {
        probeTicket=-1;
        if (Utilities.IsValid(durationProbe)) durationProbe._Cancel();
    }
    private void UpdateProbe()
    {
        if (!Networking.IsOwner(gameObject) || !playing || !Utilities.IsValid(durationProbe) || probeTicket != -1 || !CanLoad()) return;
        for (int i=0;i<queueCount;i++)
        {
            if (queueDurations[i] != 0f) continue;
            probeTicket=queueTickets[i]; lastLoad=Time.realtimeSinceStartup;
            durationProbe._Begin(queueUrls[i]); return;
        }
    }
    public void _ProbeResult(float seconds)
    {
        int ticket=probeTicket; probeTicket=-1;
        if (!Networking.IsOwner(gameObject) || ticket < 0) return;
        for(int i=0;i<queueCount;i++) if(queueTickets[i]==ticket) { queueDurations[i]=seconds>0f && !float.IsNaN(seconds) && !float.IsInfinity(seconds) ? seconds : -1f; RequestSerialization(); return; }
    }
    public override void OnDeserialization()
    {
        ApplyState();
        TryStartPlayback();
        if (playing && appliedSeekRevision != seekRevision) { appliedSeekRevision = seekRevision; SyncTime(true); }
    }
    public override void OnOwnershipTransferred(VRCPlayerApi player) { CancelProbe(); TryStartPlayback(); }
    private void ApplyState()
    {
        if (appliedRevision == revision) return;
        appliedRevision = revision;
        ResetLocalPause();
        personalIconUntil = -100f;
        appliedSeekRevision = seekRevision;
        CancelProbe();
        pending = false; ready = false; loading = false; playing = false;
        if (Utilities.IsValid(videoPlayer)) videoPlayer.Stop();
        ShowStandby();
        if (!active || VRCUrl.IsNullOrEmpty(sharedUrl)) { status = "待机中"; return; }
        pending = true; status = "等待加载";
    }
    private void Update()
    {
        UpdatePersonalView();
        UpdatePlaybackIcon(mainPlaybackIcon, mainPlaybackImage, this, mainIconUntil);
        // Pausing the owner's local decoder must not stop the shared timeline or FIFO.
        if (localPaused && active && clockStarted && Networking.IsOwner(gameObject))
        {
            float duration = GetDuration();
            if (duration > 0f && Networking.GetServerTimeInSeconds() - startTime >= duration)
                AdvanceQueue();
        }
        if (pending && CanLoad()) BeginLoad();
        if (loading && Time.realtimeSinceStartup - loadStarted > 45f)
        {
            loading = false; ready = false; playing = false;
            ResetLocalPause();
            videoPlayer.Stop(); ShowStandby(); status = "加载超时，请重新同步";
            if (Networking.IsOwner(gameObject) && queueCount > 0) AdvanceQueue();
        }
        if (ready && active && !playing) TryStartPlayback();
        if (Networking.IsOwner(gameObject) && !active && queueCount > 0) AdvanceQueue();
        UpdateProbe();
        if (playing && active && Time.realtimeSinceStartup >= nextSync)
        {
            nextSync = Time.realtimeSinceStartup + 6f;
            SyncTime(false);
        }
    }
    private void BeginLoad()
    {
        pending = false;
        if (!active || !Utilities.IsValid(videoPlayer)) return;
        lastLoad = Time.realtimeSinceStartup; loadStarted = lastLoad;
        loading = true; status = "正在加载";
        videoPlayer.LoadURL(sharedUrl);
    }
    public override void OnVideoReady()
    {
        if (!active || !loading) return;
        loading = false; ready = true;
        if (Networking.IsOwner(gameObject)) { currentDuration=GetDuration(); RequestSerialization(); }
        status = "等待同步";
        TryStartPlayback();
    }
    private void TryStartPlayback()
    {
        if (!active || !ready || playing) return;
        if (!clockStarted && Networking.IsOwner(gameObject))
        {
            startTime = Networking.GetServerTimeInSeconds();
            clockStarted = true; RequestSerialization();
        }
        if (!clockStarted) return;
        playing = true; appliedSeekRevision = seekRevision;
        videoPlayer.Play();
        SyncTime(true);
        nextSync = Time.realtimeSinceStartup + 3f;
    }
    public override void OnVideoStart()
    {
        if (!active || !ready || !playing) return;
        if (Utilities.IsValid(standbySurface)) standbySurface.SetActive(false);
        if (Utilities.IsValid(videoSurface)) videoSurface.SetActive(true);
        SyncTime(true); status = localPaused ? "本地已暂停" : "正在播放";
    }
    private void SyncTime(bool force)
    {
        float duration = GetDuration();
        if (localPaused || !clockStarted || duration <= 0f) return;
        float target = Mathf.Clamp(GetPosition(), 0f, Mathf.Max(0f, duration - .25f));
        if (force || Mathf.Abs(videoPlayer.GetTime() - target) > 1.5f) videoPlayer.SetTime(target);
    }
    public override void OnVideoEnd()
    {
        if (!active || !playing || localPaused) return;
        ResetLocalPause();
        ready = false; loading = false; playing = false;
        ShowStandby(); status = "播放结束";
        if (Networking.IsOwner(gameObject)) AdvanceQueue();
    }
    public override void OnVideoError(VideoError videoError)
    {
        if (!active || (!loading && !ready)) return;
        ResetLocalPause();
        loading = false; ready = false; playing = false;
        ShowStandby(); status = "播放失败，请检查网址";
        if (Networking.IsOwner(gameObject) && queueCount > 0) AdvanceQueue();
    }
    public void ReloadLocal()
    {
        if (!active || VRCUrl.IsNullOrEmpty(sharedUrl)) return;
        CancelProbe();
        ResetLocalPause();
        loading = false; ready = false; playing = false;
        videoPlayer.Stop(); ShowStandby(); pending = true; status = "正在等待重新同步";
    }
    private void ShowStandby()
    {
        if (Utilities.IsValid(videoSurface)) videoSurface.SetActive(false);
        if (Utilities.IsValid(standbySurface)) standbySurface.SetActive(true);
    }

    // The build linker elects exactly one input controller for the scene. No extra video
    // player, audio source, network event or synced variable is used for personal viewing.
    private bool HasPersonalViews()
    {
        return Utilities.IsValid(personalVR) && Utilities.IsValid(personalVRImage) &&
            Utilities.IsValid(personalPickup) && Utilities.IsValid(personalBody);
    }
    private void UpdatePersonalView()
    {
        if (!HasPersonalViews() || !Utilities.IsValid(Networking.LocalPlayer)) return;
        bool vr = Networking.LocalPlayer.IsUserInVR();
        if (personalOpen && (personalInVR != vr || !Utilities.IsValid(personalSource))) ClosePersonalView();
        if (!vr && Input.GetKeyDown(KeyCode.Tab)) TogglePersonalView(false);
        if (!personalOpen) return;
        RefreshPersonalImage();
        if (personalSource.GetRevision() != personalIconRevision)
        { personalIconRevision = personalSource.GetRevision(); personalIconUntil = -100f; }
        if (personalSettingsOpen) UpdatePersonalSettings();
        else
        {
            UpdatePlaybackIcon(personalPlaybackIcon, personalPlaybackImage, personalSource, personalIconUntil);
            UpdatePersonalGear();
        }
        // Some pickup implementations change the rigidbody on drop. Stop it in place.
        if (!personalPickup.IsHeld && !personalBody.isKinematic)
        {
            personalBody.velocity = Vector3.zero;
            personalBody.angularVelocity = Vector3.zero;
            personalBody.isKinematic = true;
        }
    }
    public override void InputUse(bool value, UdonInputEventArgs args)
    {
        if (!value || !HasPersonalViews() || !Utilities.IsValid(Networking.LocalPlayer) ||
            !Networking.LocalPlayer.IsUserInVR()) return;
        float now = Time.realtimeSinceStartup;
        if (now - lastPersonalToggle < .45f) return;
        // Three presses of the SAME hand, within .85s, no gap greater than .4s.
        // Presses in the opposite hand start an independent sequence.
        if (args.handType == HandType.LEFT)
        {
            if (leftTaps == 0 || now - leftFirst > .85f || now - leftLast > .4f)
            { leftTaps = 0; leftFirst = now; }
            leftLast = now;
            leftTaps++;
            if (leftTaps < 3) return;
        }
        else if (args.handType == HandType.RIGHT)
        {
            if (rightTaps == 0 || now - rightFirst > .85f || now - rightLast > .4f)
            { rightTaps = 0; rightFirst = now; }
            rightLast = now;
            rightTaps++;
            if (rightTaps < 3) return;
        }
        else return;
        leftTaps = rightTaps = 0;
        TogglePersonalView(true);
    }
    private EaststarScreen ChoosePersonalSource(Vector3 position)
    {
        EaststarScreen best = null;
        float bestDistance = float.MaxValue;
        bool bestActive = false;
        if (personalSources != null)
        {
            for (int i = 0; i < personalSources.Length; i++)
            {
                EaststarScreen candidate = personalSources[i];
                if (!Utilities.IsValid(candidate) || !candidate.gameObject.activeInHierarchy) continue;
                bool candidateActive = candidate.IsActive();
                float distance = (candidate.transform.position - position).sqrMagnitude;
                if (!Utilities.IsValid(best) || (candidateActive && !bestActive) ||
                    (candidateActive == bestActive && distance < bestDistance))
                { best = candidate; bestDistance = distance; bestActive = candidateActive; }
            }
        }
        return Utilities.IsValid(best) ? best : this;
    }
    private void TogglePersonalView(bool vr)
    {
        if (!HasPersonalViews() || Time.realtimeSinceStartup - lastPersonalToggle < .3f) return;
        lastPersonalToggle = Time.realtimeSinceStartup;
        if (personalOpen) { ClosePersonalView(); return; }
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;
        VRCPlayerApi.TrackingData head = local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        // Instantiate a separate, non-networked pickup on this client only, once.
        if (!personalVRCloned)
        {
            GameObject clone = VRCInstantiate(personalVR);
            if (!Utilities.IsValid(clone)) return;
            Transform picture = clone.transform.Find("Personal Video");
            if (!Utilities.IsValid(picture)) { Destroy(clone); return; }
            Renderer renderer = picture.GetComponent<Renderer>();
            VRCPickup pickup = clone.GetComponent<VRCPickup>();
            Rigidbody body = clone.GetComponent<Rigidbody>();
            if (!Utilities.IsValid(renderer) || !Utilities.IsValid(pickup) || !Utilities.IsValid(body))
            { Destroy(clone); return; }
            personalVR = clone;
            personalVRImage = renderer;
            personalPickup = pickup;
            personalBody = body;
            Transform icon = clone.transform.Find("Local Playback UI/Playback Icon");
            if (Utilities.IsValid(icon))
            {
                personalPlaybackIcon = icon.GetComponent<CanvasGroup>();
                Transform artwork = icon.Find("Artwork");
                if (Utilities.IsValid(artwork)) personalPlaybackImage = artwork.GetComponent<RawImage>();
            }
            BindPersonalSettings(clone);
            personalVRCloned = true;
        }
        personalSource = ChoosePersonalSource(head.position);
        personalIconRevision = personalSource.GetRevision();
        personalShownMaterial = null;
        personalIconUntil = -100f;
        personalGearUntil = -100f;
        personalInVR = vr;
        personalOpen = true;
        personalSettingsOpen = false;
        personalSeekPending = false;
        personalQueuePage = 0;
        personalMessageUntil = 0f;
        if (Utilities.IsValid(personalPanel)) personalPanel.SetActive(false);
        if (Utilities.IsValid(personalGear)) personalGear.SetActive(false);
        if (Utilities.IsValid(personalReveal)) personalReveal.SetActive(true);
        ResetPersonalAddress();
        RefreshPersonalImage();
        // Same AudioSource, not a second stream. Temporarily make custom 3D audio local 2D.
        personalAudio = personalSource.audioSource;
        if (Utilities.IsValid(personalAudio))
        {
            personalOriginalBlend = personalAudio.spatialBlend;
            personalAudioChanged = true;
            personalAudio.spatialBlend = 0f;
        }
        // Same world-space placement and pickup for PC and VR. No camera parenting.
        personalBody.isKinematic = true;
        personalVR.transform.SetPositionAndRotation(head.position + head.rotation * Vector3.forward * .85f, head.rotation);
        personalVR.SetActive(true);
        UpdatePlaybackIcon(personalPlaybackIcon, personalPlaybackImage, personalSource, personalIconUntil);
    }
    // The cloned world-space panel binds to this existing local controller. No new decoder or synced state.
    private void BindPersonalSettings(GameObject clone)
    {
        Transform ui = clone.transform.Find("Local Playback UI");
        if (!Utilities.IsValid(ui)) return;
        Transform gear = ui.Find("Personal Settings");
        Transform reveal = ui.Find("Reveal Playback Icon");
        Transform panel = ui.Find("Personal Console");
        if (!Utilities.IsValid(gear) || !Utilities.IsValid(panel) || !Utilities.IsValid(reveal)) return;
        personalGear = gear.gameObject;
        personalGearGroup = gear.GetComponent<CanvasGroup>();
        personalReveal = reveal.gameObject;
        personalPanel = panel.gameObject;
        personalInput = panel.Find("URL").GetComponent<VRCUrlInputField>();
        personalStatus = panel.Find("Status").GetComponent<Text>();
        personalQueue = panel.Find("Queue").GetComponent<Text>();
        personalTime = panel.Find("Time").GetComponent<Text>();
        personalPageText = panel.Find("Page").GetComponent<Text>();
        personalPauseButton = panel.Find("本地暂停").GetComponent<Button>();
        personalPauseText = panel.Find("本地暂停/Label").GetComponent<Text>();
        personalSkipButton = panel.Find("跳过视频").GetComponent<Button>();
        personalStopButton = panel.Find("停止并清空").GetComponent<Button>();
        personalPrevButton = panel.Find("上一页").GetComponent<Button>();
        personalNextButton = panel.Find("下一页").GetComponent<Button>();
        personalVolume = panel.Find("Volume").GetComponent<Slider>();
        personalProgress = panel.Find("Progress").GetComponent<Slider>();
    }
    public void OpenPersonalSettings()
    {
        if (!personalOpen || !Utilities.IsValid(personalSource) || !Utilities.IsValid(personalPanel)) return;
        personalSettingsOpen = true;
        personalGearUntil = -100f;
        personalGear.SetActive(false);
        personalReveal.SetActive(false);
        if (Utilities.IsValid(personalPlaybackIcon)) personalPlaybackIcon.gameObject.SetActive(false);
        personalPanel.SetActive(true);
        personalRefreshAt = 0f;
        RefreshPersonalSettings();
    }
    public void ClosePersonalSettings()
    {
        if (!personalOpen || !personalSettingsOpen) return;
        personalSettingsOpen = false;
        personalSeekPending = false;
        personalPanel.SetActive(false);
        personalReveal.SetActive(true);
        personalIconUntil = Time.realtimeSinceStartup + 3f;
        personalGearUntil = Time.realtimeSinceStartup + 3f;
        personalGear.SetActive(true);
        if (Utilities.IsValid(personalGearGroup))
        {
            personalGearGroup.alpha = 1f;
            personalGearGroup.interactable = true;
            personalGearGroup.blocksRaycasts = true;
        }
    }
    private bool PersonalPanelReady()
    { return personalOpen && personalSettingsOpen && Utilities.IsValid(personalSource) && Utilities.IsValid(personalPanel); }
    private void PersonalNotice(string value)
    { personalMessage = value; personalMessageUntil = Time.realtimeSinceStartup + 4f; personalRefreshAt = 0f; }
    public void ResetPersonalAddress()
    {
        if (!personalOpen || !Utilities.IsValid(personalInput) || !Utilities.IsValid(personalSource)) return;
        VRCUrl prefix = personalSource.personalDefaultAddress;
        personalInput.SetUrl(VRCUrl.IsNullOrEmpty(prefix) ? VRCUrl.Empty : prefix);
    }
    public void AddPersonalAddress()
    {
        if (!PersonalPanelReady() || !Utilities.IsValid(personalInput)) return;
        VRCUrl url = personalInput.GetUrl();
        if (VRCUrl.IsNullOrEmpty(url)) { PersonalNotice("请输入视频网址"); return; }
        string address = url.Get();
        VRCUrl prefix = personalSource.personalDefaultAddress;
        if (!VRCUrl.IsNullOrEmpty(prefix) && (!address.StartsWith(prefix.Get()) || address.Length <= prefix.Get().Length))
        { PersonalNotice("请保留解析前缀，在末尾粘贴网址"); return; }
        if ((!address.StartsWith("https://") && !address.StartsWith("http://")) || address.Length > 2048)
        { PersonalNotice("请输入有效的网址"); return; }
        if (personalSource.GetQueueCount() >= 20) { PersonalNotice("播放列表已满"); return; }
        if (personalSource.Submit(url)) { ResetPersonalAddress(); PersonalNotice("已发送入队请求"); }
        else PersonalNotice("提交失败，请重试");
    }
    public void PersonalTogglePause() { if (PersonalPanelReady()) { personalSeekPending = false; personalSource.ToggleLocalPause(); personalRefreshAt = 0f; } }
    public void PersonalResync() { if (PersonalPanelReady()) { personalSeekPending = false; personalSource.ReloadLocal(); personalRefreshAt = 0f; } }
    public void PersonalSkip() { if (PersonalPanelReady() && Networking.IsMaster) { personalSeekPending = false; personalSource._SkipForEveryone(); personalRefreshAt = 0f; } }
    public void PersonalStop() { if (PersonalPanelReady() && Networking.IsMaster) { personalSeekPending = false; personalSource._StopForEveryone(); personalRefreshAt = 0f; } }
    public void PersonalPreviousPage() { if (PersonalPanelReady() && personalQueuePage > 0) { personalQueuePage--; personalRefreshAt = 0f; } }
    public void PersonalNextPage() { if (PersonalPanelReady() && (personalQueuePage + 1)*3 < personalSource.GetQueueCount()) { personalQueuePage++; personalRefreshAt = 0f; } }
    public void PersonalVolumeChanged()
    { if (!personalUpdating && PersonalPanelReady() && Utilities.IsValid(personalVolume)) personalSource.SetLocalVolume(personalVolume.value); }
    public void PersonalProgressChanged()
    {
        if (personalUpdating || !PersonalPanelReady() || !Utilities.IsValid(personalProgress) || !personalSource.CanSeek()) return;
        personalSeekFraction = personalProgress.value;
        personalSeekRevision = personalSource.GetRevision();
        personalSeekPending = true;
        personalSeekAt = Time.realtimeSinceStartup + .4f;
        personalRefreshAt = 0f;
    }
    private void UpdatePersonalSettings()
    {
        if (!PersonalPanelReady()) return;
        if (personalSeekPending)
        {
            if (!personalSource.CanSeek() || personalSeekRevision != personalSource.GetRevision()) personalSeekPending = false;
            else if (Time.realtimeSinceStartup >= personalSeekAt)
            { personalSeekPending = false; personalSource.Seek(personalSeekFraction, personalSeekRevision); }
        }
        if (Time.realtimeSinceStartup >= personalRefreshAt) RefreshPersonalSettings();
    }
    private string PersonalTime(float seconds)
    {
        int total = Mathf.FloorToInt(Mathf.Max(0f, seconds));
        int h = total / 3600; int m = total / 60 % 60; int sec = total % 60;
        return (h > 0 ? h + ":" : "") + (m < 10 ? "0" : "") + m + ":" + (sec < 10 ? "0" : "") + sec;
    }
    private string PersonalShort(string address)
    {
        int marker = address.IndexOf("bilibili.com/video/");
        if (marker >= 0) address = address.Substring(marker);
        return address.Length > 43 ? address.Substring(0, 40) + "..." : address;
    }
    private void RefreshPersonalSettings()
    {
        if (!PersonalPanelReady()) return;
        personalRefreshAt = Time.realtimeSinceStartup + .25f;
        int count = personalSource.GetQueueCount();
        int last = count > 0 ? (count - 1)/3 : 0;
        if (personalQueuePage > last) personalQueuePage = last;
        personalStatus.text = Time.realtimeSinceStartup < personalMessageUntil ? personalMessage : personalSource.GetStatus();
        personalPauseButton.interactable = personalSource.CanLocalPause();
        personalPauseText.text = personalSource.IsLocalPaused() ? "恢复播放" : "本地暂停";
        personalSkipButton.interactable = Networking.IsMaster && (personalSource.IsActive() || count > 0);
        personalStopButton.interactable = personalSkipButton.interactable;
        personalPrevButton.interactable = personalQueuePage > 0;
        personalNextButton.interactable = personalQueuePage < last;
        personalPageText.text = (personalQueuePage+1) + "/" + (last+1);
        float duration = personalSource.GetDuration();
        float pos = personalSeekPending ? personalSeekFraction*duration : personalSource.GetLocalPosition();
        personalTime.text = duration > 0f ? PersonalTime(pos) + " / " + PersonalTime(duration) : "--:-- / --:--";
        personalUpdating = true;
        personalVolume.value = personalSource.GetLocalVolume();
        personalProgress.interactable = personalSource.CanSeek();
        if (!personalSeekPending) personalProgress.value = duration > 0f ? Mathf.Clamp01(pos/duration) : 0f;
        personalUpdating = false;
        string rows = "播放列表 (" + count + "/20)\n";
        rows += personalSource.IsActive() ? "当前：" + PersonalShort(personalSource.GetCurrentAddress()) + "\n添加者：" + personalSource.GetCurrentName() + "\n" : "当前没有视频\n";
        for (int n = 0; n < 3; n++)
        {
            int i = personalQueuePage*3+n;
            if (i >= count) break;
            float d = personalSource.GetQueueDuration(i);
            rows += (i+1) + ". " + PersonalShort(personalSource.GetQueueAddress(i)) + "\n" + personalSource.GetQueueName(i) + " | " + (d > 0 ? PersonalTime(d) : d < 0 ? "未知" : "读取中") + "\n";
        }
        personalQueue.text = rows;
    }
    private void RefreshPersonalImage()
    {
        if (!Utilities.IsValid(personalSource)) return;
        GameObject surface = personalSource.IsShowingVideo() ? personalSource.videoSurface : personalSource.standbySurface;
        if (!Utilities.IsValid(surface)) return;
        Renderer sourceRenderer = surface.GetComponent<Renderer>();
        if (!Utilities.IsValid(sourceRenderer)) return;
        Material mat = sourceRenderer.sharedMaterial;
        if (!Utilities.IsValid(mat) || mat == personalShownMaterial) return;
        personalShownMaterial = mat;
        personalVRImage.sharedMaterial = mat;
    }
    private void ClosePersonalView()
    {
        if (Utilities.IsValid(personalPickup) && personalPickup.IsHeld) personalPickup.Drop(Networking.LocalPlayer);
        if (Utilities.IsValid(personalVR)) personalVR.SetActive(false);
        if (personalAudioChanged && Utilities.IsValid(personalAudio)) personalAudio.spatialBlend = personalOriginalBlend;
        personalAudioChanged = false;
        personalAudio = null;
        personalOpen = false;
        personalSettingsOpen = false;
        personalSeekPending = false;
        personalIconUntil = -100f;
        personalGearUntil = -100f;
        personalSource = null;
        personalShownMaterial = null;
        leftTaps = rightTaps = 0;
    }
    private void OnDisable() { if (personalOpen) ClosePersonalView(); }
}
