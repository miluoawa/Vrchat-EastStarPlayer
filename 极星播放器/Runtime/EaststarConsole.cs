using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDK3.Components;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class EaststarConsole : UdonSharpBehaviour
{
    [Header("必需的显示屏")]
    public EaststarScreen screen;
    [Header("默认解析地址（包含等号等分隔符）")]
    [Tooltip("例如 https://123456.com/?url= 。留空为直接播放。游戏中需保留前缀并在末尾粘贴视频地址。")]
    public VRCUrl defaultInputAddress = VRCUrl.Empty;
    [Header("界面组件")]
    public VRCUrlInputField urlInput;
    public Text statusText, prefixText, progressText, controllerText;
    public Slider volumeSlider, progressSlider;
    public Text queueHeader, queuePageText, nowPlayingText;
    public Text[] queueRows;
    public Button skipButton, stopButton, prevButton, nextButton;
    public Button localPauseButton;
    public Text localPauseLabel;
    private int queuePage;
    private string message = "";
    private float messageUntil, nextRefresh, seekAt, preview;
    private int previewRevision;
    private bool updatingProgress, seekPending, updatingVolume;
    private void Start()
    {
        ResetAddress();
        if (Utilities.IsValid(prefixText)) prefixText.text = VRCUrl.IsNullOrEmpty(defaultInputAddress) ? "模式：直接播放" : "解析前缀：" + defaultInputAddress.Get();
        ChangeVolume(); Refresh();
    }
    private void Update()
    {
        if (seekPending)
        {
            if (!Utilities.IsValid(screen) || !screen.CanSeek() || screen.GetRevision() != previewRevision) seekPending = false;
            else if (Time.realtimeSinceStartup >= seekAt)
            {
                seekPending = false;
                if (!screen.Seek(preview, previewRevision)) Notify("无法调整进度");
                nextRefresh = 0f;
            }
        }
        if (Time.realtimeSinceStartup >= nextRefresh) { nextRefresh = Time.realtimeSinceStartup + .2f; Refresh(); }
    }
    private void Refresh()
    {
        bool linked = Utilities.IsValid(screen);
        RefreshQueue(linked);
        if (linked && Utilities.IsValid(volumeSlider))
        {
            updatingVolume = true;
            volumeSlider.value = screen.GetLocalVolume();
            updatingVolume = false;
        }
        bool canSeek = linked && screen.CanSeek();
        float duration = linked ? screen.GetDuration() : 0f;
        float position = linked ? screen.GetLocalPosition() : 0f;
        if (Utilities.IsValid(localPauseButton)) localPauseButton.interactable = linked && screen.CanLocalPause();
        if (Utilities.IsValid(localPauseLabel)) localPauseLabel.text = linked && screen.IsLocalPaused() ? "恢复播放" : "本地暂停";
        if (Utilities.IsValid(statusText)) statusText.text = !linked ? "未连接显示屏" : (Time.realtimeSinceStartup < messageUntil ? message : screen.GetStatus());
        if (Utilities.IsValid(controllerText)) controllerText.text = !linked ? "请连接显示屏" : linked && duration <= 0f && screen.IsInitiator() ? "等待视频加载或视频不支持进度调整" : screen.GetControllerText();
        if (Utilities.IsValid(progressSlider))
        {
            progressSlider.interactable = canSeek;
            if (!seekPending)
            {
                updatingProgress = true;
                progressSlider.value = duration > 0f ? Mathf.Clamp01(position / duration) : 0f;
                updatingProgress = false;
            }
        }
        if (Utilities.IsValid(progressText)) progressText.text = duration > 0f ? FormatTime(seekPending ? preview * duration : position) + " / " + FormatTime(duration) : "--:-- / --:--";
    }
    private string FormatTime(float value)
    {
        int total = Mathf.FloorToInt(Mathf.Max(0f, value));
        int hours = total / 3600; int minutes = (total / 60) % 60; int seconds = total % 60;
        string m = (minutes < 10 ? "0" : "") + minutes;
        string s = (seconds < 10 ? "0" : "") + seconds;
        return hours > 0 ? hours + ":" + m + ":" + s : m + ":" + s;
    }
    public void ChangeProgress()
    {
        if (updatingProgress || !Utilities.IsValid(progressSlider) || !Utilities.IsValid(screen) || !screen.CanSeek()) return;
        preview = progressSlider.value;
        previewRevision = screen.GetRevision();
        seekPending = true;
        seekAt = Time.realtimeSinceStartup + .4f;
        Refresh();
    }
    private void Notify(string value) { message = value; messageUntil = Time.realtimeSinceStartup + 5f; }
    public void ResetAddress()
    {
        if (Utilities.IsValid(urlInput)) urlInput.SetUrl(VRCUrl.IsNullOrEmpty(defaultInputAddress) ? VRCUrl.Empty : defaultInputAddress);
    }
    public void PlayAddress()
    {
        if (!Utilities.IsValid(screen) || !Utilities.IsValid(urlInput)) { Notify("未连接显示屏或输入框"); return; }
        VRCUrl url = urlInput.GetUrl();
        if (VRCUrl.IsNullOrEmpty(url)) { Notify("请输入视频网址"); return; }
        string address = url.Get();
        if (!VRCUrl.IsNullOrEmpty(defaultInputAddress))
        {
            string prefix = defaultInputAddress.Get();
            if (!address.StartsWith(prefix) || address.Length <= prefix.Length) { Notify("请保留解析前缀，并在末尾粘贴视频网址"); return; }
        }
        if (!address.StartsWith("https://") && !address.StartsWith("http://")) { Notify("请输入有效的 HTTP(S) 网址"); return; }
        if (address.Length > 2048) { Notify("网址过长"); return; }
        if (screen.GetQueueCount() >= 20) { Notify("播放列表已满"); return; }
        if (screen.Submit(url)) { seekPending = false; ResetAddress(); Notify("已发送入队请求"); }
        else Notify("提交失败，请重试");
    }
    public void StopPlayback() { if (!Networking.IsMaster) return; seekPending = false; if (Utilities.IsValid(screen)) screen._StopForEveryone(); }
    public void SkipVideo() { if (!Networking.IsMaster) return; seekPending=false; if (Utilities.IsValid(screen)) screen._SkipForEveryone(); }
    public void PreviousPage() { if(queuePage>0) queuePage--; Refresh(); }
    public void NextPage() { if(Utilities.IsValid(screen) && (queuePage+1)*5<screen.GetQueueCount()) queuePage++; Refresh(); }
    private string ShortAddress(string address)
    {
        int marker=address.IndexOf("bilibili.com/video/");
        if(marker>=0) address=address.Substring(marker);
        if(address.Length>58) return address.Substring(0,55)+"...";
        return address;
    }
    private void RefreshQueue(bool linked)
    {
        int count=linked?screen.GetQueueCount():0;
        int last=count>0?(count-1)/5:0;
        if(queuePage>last) queuePage=last;
        if(Utilities.IsValid(queueHeader)) queueHeader.text="播放列表  ("+count+"/20)";
        if(Utilities.IsValid(queuePageText)) queuePageText.text=(queuePage+1)+" / "+(last+1);
        if(Utilities.IsValid(nowPlayingText)) nowPlayingText.text=linked&&screen.IsActive()?"当前视频："+ShortAddress(screen.GetCurrentAddress())+"\n添加者："+screen.GetCurrentName():"当前没有视频";
        if(Utilities.IsValid(skipButton)) skipButton.interactable=linked&&Networking.IsMaster&&(screen.IsActive()||count>0);
        if(Utilities.IsValid(stopButton)) stopButton.interactable=linked&&Networking.IsMaster&&(screen.IsActive()||count>0);
        if(Utilities.IsValid(prevButton)) prevButton.interactable=queuePage>0;
        if(Utilities.IsValid(nextButton)) nextButton.interactable=queuePage<last;
        if(queueRows==null) return;
        for(int row=0;row<queueRows.Length;row++)
        {
            if(!Utilities.IsValid(queueRows[row])) continue;
            int i=queuePage*5+row;
            if(i>=count) { queueRows[row].text=row==0&&count==0?"暂无待播放视频":""; continue; }
            float duration=screen.GetQueueDuration(i);
            string time=duration>0f?FormatTime(duration):duration<0f?"未知":"读取中";
            queueRows[row].text=(i+1)+". "+ShortAddress(screen.GetQueueAddress(i))+"\n添加者："+screen.GetQueueName(i)+"  |  时长："+time;
        }
    }
    public void ToggleLocalPause()
    {
        seekPending = false;
        if (Utilities.IsValid(screen)) screen.ToggleLocalPause();
        Refresh();
    }
    public void Resync() { seekPending = false; if (Utilities.IsValid(screen)) screen.ReloadLocal(); }
    public void ChangeVolume() { if (!updatingVolume && Utilities.IsValid(screen) && Utilities.IsValid(volumeSlider)) screen.SetLocalVolume(volumeSlider.value); }
}
