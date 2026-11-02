Imports System.Collections.ObjectModel
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports PCL

Public Class PageLinkMain

    '===============================
    '  状态机与前端页面
    '===============================

#Region "状态管理"

    Public Enum LinkStates
        Waiting
        Loading
        Failed
        Finished
    End Enum
    Public Shared LinkState As LinkStates = LinkStates.Waiting

    ''' <summary>
    ''' 切换到指定的状态。
    ''' </summary>
    Private Sub ChangeState(NewState As LinkStates)
        If LinkState = NewState Then Return
        Dim OldState = LinkState
        LinkState = NewState
        Logger.Info($"主状态由 {OldState} 变更为 {NewState}")
        '触发状态切换
        RunInUi(
        Sub()
            Select Case NewState
                Case LinkStates.Waiting
                    SwitchToWaiting(OldState)
                Case LinkStates.Loading
                    SwitchToLoading(OldState)
                Case LinkStates.Failed
                    SwitchToFailed(OldState)
                Case LinkStates.Finished
                    SwitchToFinished(OldState)
            End Select
        End Sub)
    End Sub

    '让加载器的 成功/失败 事件触发状态切换
    Private Sub LoaderInit() Handles Me.Initialized
        PageLoaderInit(Load, PanLoad, PanContent, PanAlways, LinkLoader, AutoRun:=False)
        AddHandler LinkLoader.OnStateChangedUi,
        Sub(Loader As LoaderBase, NewState As LoadState, OldState As LoadState)
            Select Case NewState
                Case LoadState.Finished
                    ChangeState(LinkStates.Finished)
                Case LoadState.Failed
                    Telemetry("联机失败", "Exception", FilterUserName(Loader.Error.GetDisplay(True), "*"))
                    ChangeState(LinkStates.Failed)
            End Select
        End Sub
    End Sub

    '页面动画兼容
    Private Sub UpdatePanelVisibility() Handles Me.PageEnter
        FrmLinkMain.PanSelect.Visibility = If(LinkState = LinkStates.Waiting, Visibility.Visible, Visibility.Collapsed)
        FrmLinkMain.PanFinish.Visibility = If(LinkState = LinkStates.Finished, Visibility.Visible, Visibility.Collapsed)
    End Sub

#End Region

#Region "初始页面 (Waiting)"

    '由任意状态切换到 Waiting
    Private Sub SwitchToWaiting(OldState As LinkStates)
        '加载器与进程状态
        LinkLoader.Cancel()
        SyncLock LinkLoader.LockState
            LinkLoader.State = LoadState.Waiting
        End SyncLock
        ProcessStop()
        '重置联机信息
        SyncLock ModuleLock
            ModuleInvite = Nothing
            ModuleReady = Nothing
            ModuleError = Nothing
            ModuleConnected = False
            ModulePeople = 1
            ModulePing = 0
            ModuleLogs.Clear()
        End SyncLock
        '页面切换
        If OldState <> LinkStates.Finished Then
            '从完成页面退出时，PageOnContentExit 结束会触发 PageEnter 事件，这会调用 UpdatePanelVisibility
            '如果在现在提前调用了，会丢失完成页面的退出动画
            UpdatePanelVisibility()
        End If
        PageOnContentExit()
    End Sub

    '===============================
    '  页面 UI
    '===============================

    '创建（作为房主）
    Private Sub Create_MouseLeftButtonUp() Handles PanSelectCreate.MouseLeftButtonUp
        '输入端口号
        Dim Port As String = MyMsgBoxInput("输入端口", $"在单人游戏的暂停菜单选择 {vbLQ}对局域网开放{vbRQ}，然后输入端口数字。{vbCrLf}联机会通过 Steam 网络把这个端口转发给朋友，不需要公网 IP 或端口映射。",
            ValidateRules:=New Collection(Of Validate) From {New ValidateInteger(1024, 65535)},
            HintText:="端口号")
        If Port Is Nothing Then Return
        '基础信息
        IsServerSide = True
        ServerPort = Port
        Logger.Info($"尝试创建联机房间，端口 {ServerPort}")
        '启动
        ChangeState(LinkStates.Loading)
    End Sub

    '加入（作为客户端）
    Private Shared LastCode As String = Nothing
    ''' <summary>
    ''' 输入房间号，切换到联机页并立即加入房间。
    ''' </summary>
    Public Shared Sub Join() Handles PanSelectJoin.MouseLeftButtonUp
        Dim Code As String = MyMsgBoxInput("输入房间号", "输入房主发给你的房间号（17 位 Steam ID）。",
            HintText:=If(String.IsNullOrEmpty(LastCode), "", "使用上一次的房间号"))
        If Not String.IsNullOrEmpty(LastCode) AndAlso Code IsNot Nothing AndAlso Code = "" Then Code = LastCode
        If Code Is Nothing Then Return
        Join(Code)
    End Sub
    Public Sub JoinInternal(Code As String)
        If Code Is Nothing Then Return
        '基础格式校验
        Dim ValidateResult = ValidateCodeFormat(Code)
        If ValidateResult IsNot Nothing Then
            Hint(ValidateResult, HintType.Red)
            Return
        End If
        Code = FixCodeFormat(Code)
        Logger.Info($"实际使用的房间号：{Code}")
        '基础信息
        IsServerSide = False
        HostSteamId = Code
        '启动
        LastCode = Code
        ChangeState(LinkStates.Loading)
    End Sub
    Public Shared Function ValidateCodeFormat(Code As String) As String
        If Code Is Nothing Then Return "房间号为空！"
        Code = FixCodeFormat(Code)
        Dim Id As ULong
        If Code.Length <> 17 OrElse Not ULong.TryParse(Code, Id) Then
            Return "房间号有误，应该是 17 位数字（房主的 Steam ID）！"
        End If
        If Not Code.StartsWithF("7656") Then Return "房间号有误，应该是 17 位数字（房主的 Steam ID）！"
        Return Nothing
    End Function
    Private Shared Function FixCodeFormat(Code As String) As String
        Code = Code.Between("【", "】").Between("[", "]") '从完整消息中提取
        '只保留数字
        Dim Result As New StringBuilder
        For Each c As Char In Code
            If Char.IsDigit(c) Then Result.Append(c)
        Next
        Return Result.ToString()
    End Function

    '自动加入

    ''' <summary>
    ''' 切换到联机页并立即加入指定房间。
    ''' </summary>
    Public Shared Sub Join(Code As String)
        If LinkState <> LinkStates.Waiting Then
            Hint("你已经在联机房间中了！", HintType.Red)
        ElseIf FrmMain.PageCurrent = FormMain.PageType.Link Then
            FrmLinkMain.JoinInternal(Code)
        Else
            AutoJoinCode = Code
            FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
        End If
    End Sub
    '在进入页面时自动尝试加入房间
    Private Shared AutoJoinCode As String = Nothing
    Private Sub PanSelectJoin_Loaded() Handles PanSelectJoin.Loaded
        If AutoJoinCode Is Nothing Then Return
        If LinkState = LinkStates.Waiting Then JoinInternal(AutoJoinCode)
        AutoJoinCode = Nothing
    End Sub

#End Region

#Region "加载页面 (Loading | Failed)"

    '由 Waiting 或 Failed 状态切换到 Loading
    Private Sub SwitchToLoading(OldState As LinkStates)
        '清理变量
        SyncLock ModuleLock
            ModuleInvite = Nothing
            ModuleReady = Nothing
            ModuleError = Nothing
            ModuleConnected = False
            ModulePeople = 1
            ModulePing = 0
            ModuleLogs.Clear()
        End SyncLock
        '加载器与进程状态
        LinkLoader.Start(IsForceRestart:=True)
        '页面切换会由 Loader 调用 MyPageRight 来触发
        'UI 更新
        UpdateProgressBar(0)
        LabLoadTitle.Text = If(IsServerSide, "创建房间中", "加入房间中")
        UpdateLoadingPage("正在初始化……", "准备初始化")
    End Sub

    '由 Loading 状态切换到 Failed
    Private Sub SwitchToFailed(OldState As LinkStates)
        '加载器与进程状态
        LinkLoader.Cancel()
        ProcessStop()
        'UI 更新
        UpdateProgressBar(1)
        '显示错误信息
        LabLoadTitle.Text = FailReason
        Dim Brief As String = LinkLoader.Error.GetDisplay(False)
        Dim InnerEx As Exception = LinkLoader.Error
        If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Do Until InnerEx.InnerException Is Nothing
            InnerEx = InnerEx.InnerException
            If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Loop
        LabLoadDesc.Text = If(Brief.StartsWithF("$"), Brief.TrimStart("$"), LinkLoader.Error.GetDisplay(True))
        Logger.Warn(LinkLoader.Error, LabLoadTitle.Text)
    End Sub

    '===============================
    '  页面 UI
    '===============================

    Private Sub UpdateLoadingPage(Title As String, FailBrief As String)
        If FailReason = FailBrief & "失败" Then Return
        FailReason = FailBrief & "失败"
        Logger.Info($"开始步骤：{Title}")
        RunInUiWait(
        Sub()
            If FrmLinkMain Is Nothing OrElse Not FrmLinkMain.LabLoadDesc.IsLoaded Then Return
            FrmLinkMain.LabLoadDesc.Text = Title
        End Sub)
    End Sub
    ''' <summary>
    ''' 由联机模块上报的状态文字更新加载页说明。
    ''' </summary>
    Private Sub UpdateLoadingState(Title As String)
        If String.IsNullOrWhiteSpace(Title) Then Return
        RunInUi(
        Sub()
            If FrmLinkMain Is Nothing OrElse Not FrmLinkMain.LabLoadDesc.IsLoaded Then Return
            FrmLinkMain.LabLoadDesc.Text = Title
        End Sub)
    End Sub
    Private FailReason As String = "准备初始化"

    '取消加载
    Private Sub BtnLoadCancel_Click() Handles BtnLoadCancel.Click
        ChangeState(LinkStates.Waiting)
    End Sub

    '点击重试
    Private Sub CardLoad_MouseLeftButtonUp() Handles CardLoad.MouseLeftButtonUp
        ChangeState(LinkStates.Loading)
    End Sub

    '进度条
    Private Sub UpdateProgressBar(Optional Value As Double = -1)
        If Value = -1 Then Value = LinkLoader.Progress
        Dim DisplayingProgress As Double = ColumnProgressA.Width.Value
        If Math.Round(Value - DisplayingProgress, 3) = 0 Then Return
        If DisplayingProgress <= Value Then
            Dim NewProgress As Double = If(Value = 1, 1, DisplayingProgress +
                (Value - DisplayingProgress) ^ 2 * 0.5)
            AniStart({
                AaGridLengthWidth(ColumnProgressA, NewProgress - ColumnProgressA.Width.Value, 200),
                AaGridLengthWidth(ColumnProgressB, (1 - NewProgress) - ColumnProgressB.Width.Value, 200)
            }, "Link Progress")
        Else
            ColumnProgressA.Width = New GridLength(Value, GridUnitType.Star)
            ColumnProgressB.Width = New GridLength(1 - Value, GridUnitType.Star)
            AniStop("Link Progress")
        End If
    End Sub
    Private Sub CardLoad_SizeChanged() Handles CardLoad.SizeChanged
        RectProgressClip.Rect = New Rect(0, 0, CardLoad.ActualWidth, 12)
    End Sub

#End Region

#Region "完成页面 (Finished)"

    '由 Loading 状态切换到 Finished
    Private Sub SwitchToFinished(OldState As LinkStates)
        '由于只可能从加载器的完成事件触发，不需要管加载器
        UpdatePanelVisibility() '页面的实际切换会由 Loader 调用 MyPageRight 来触发

        'UI 更新
        If IsServerSide Then
            LabFinishTitle.Text = "已创建房间"
            LabFinishDesc.Text = $"把房间号发给朋友，让大家加入房间吧！{vbCrLf}房间号：{GetInviteCode()}"
            BtnFinishExit.Text = "关闭"
            BtnFinishPing.ToolTip = "网络延迟"
            BtnFinishCopy.Visibility = Visibility.Visible
            Copy() '立即复制房间号
            '下边栏
            BtnFinishPort.Visibility = Visibility.Visible
            LabFinishPort.Visibility = Visibility.Visible
            BtnFinishIp.Visibility = Visibility.Collapsed
            LabFinishIp.Visibility = Visibility.Collapsed
            LabFinishPort.Text = ServerPort
        Else
            LabFinishTitle.Text = "已加入房间"
            LabFinishDesc.Text = $"在多人游戏页面的最下方就能找到联机房间！{vbCrLf}注意：使用离线登录时不要手动输入 IP！"
            BtnFinishExit.Text = "离开"
            BtnFinishPing.ToolTip = "与房主的延迟"
            BtnFinishCopy.Visibility = Visibility.Collapsed
            '下边栏
            BtnFinishPort.Visibility = Visibility.Collapsed
            LabFinishPort.Visibility = Visibility.Collapsed
            LabFinishIp.Text = ClientAddress
            BtnFinishIp.Visibility = Visibility.Visible
            LabFinishIp.Visibility = Visibility.Visible
        End If
        Update() '立即刷新
    End Sub

    '===============================
    '  页面 UI
    '===============================

    '退出
    Private Sub BtnFinishExit_Click() Handles BtnFinishExit.Click
        TryExit(False, False)
    End Sub
    ''' <summary>
    ''' 退出联机。
    ''' 返回是否弹出了警告窗口并且玩家选择了取消。
    ''' </summary>
    Public Function TryExit(Silent As Boolean, Closing As Boolean) As Boolean
        If LinkState = LinkStates.Waiting OrElse LinkState = LinkStates.Failed Then Return False
        If Not Silent Then
            If IsServerSide AndAlso PeopleCount > 1 Then
                If MyMsgBox("你确定要关闭联机房间吗？" & vbCrLf & "所有玩家都需要重新输入房间号才可加入游戏！", "退出联机", "确定", "取消", IsWarn:=True) = 2 Then Return True
            ElseIf Closing Then
                If MyMsgBox(If(IsServerSide, "你确定要关闭联机房间吗？", "你确定要离开联机房间吗？"), "退出联机", "确定", "取消") = 2 Then Return True
            End If
        End If
        ChangeState(LinkStates.Waiting)
        Return False
    End Function

    '复制房间号
    Private Sub Copy() Handles BtnFinishCopy.Click
        Dim CodeText As String = $"在 PCL 启动器中输入房间号【{GetInviteCode()}】，即可加入联机房间！"
        ClipboardSet(CodeText, False)
        Settings.Set("LinkLastAutoJoinInviteCode", CodeText)
        Hint("已复制房间号！", HintType.Green)
    End Sub
    Private Function GetInviteCode() As String
        Return If(ModuleInvite, "")
    End Function

    '复制 IP
    Private Sub BtnFinishIp_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs) Handles BtnFinishIp.MouseLeftButtonUp
        ClipboardSet(ClientAddress, False)
        Hint("已复制服务器地址！", HintType.Green)
    End Sub

#End Region

    '===============================
    '  后端
    '===============================

    ''' <summary>
    ''' 联机模块（PCLink.exe）的路径。
    ''' </summary>
    Private PathLinkModule As String = Paths.AppDataThenName & "PCLink\PCLink.exe"
    ''' <summary>
    ''' 当前是服务端还是客户端。
    ''' </summary>
    Private IsServerSide As Boolean
    ''' <summary>
    ''' 服务端映射前的端口号。
    ''' </summary>
    Private ServerPort As Integer
    ''' <summary>
    ''' 客户端映射后的端口号。
    ''' </summary>
    Private ClientPort As Integer
    ''' <summary>
    ''' 在客户端映射后的地址。
    ''' 除非已作为客户端启动联机模块，否则一直为 Nothing。
    ''' </summary>
    Private ClientAddress As String = Nothing
    ''' <summary>
    ''' 房主的 Steam ID（客户端的房间号）。
    ''' </summary>
    Private HostSteamId As String = Nothing

    '===== 由联机模块上报的信息 =====

    Private ReadOnly ModuleLock As New Object
    ''' <summary>
    ''' 房主创建的房间号，即房主的 Steam ID；客户端为 Nothing。
    ''' </summary>
    Private ModuleInvite As String = Nothing
    ''' <summary>
    ''' 客户端本地的游戏入口地址，如 127.0.0.1:52345。
    ''' </summary>
    Private ModuleReady As String = Nothing
    ''' <summary>
    ''' 是否已与房主建立连接。
    ''' </summary>
    Private ModuleConnected As Boolean = False
    ''' <summary>
    ''' 当前房间人数，包括自己。
    ''' </summary>
    Private ModulePeople As Integer = 1
    ''' <summary>
    ''' 与房主的延迟 (ms)，0 代表尚不可用。
    ''' </summary>
    Private ModulePing As Double = 0
    ''' <summary>
    ''' 联机模块给出的错误信息，Nothing 代表没有错误。
    ''' </summary>
    Private ModuleError As String = Nothing
    ''' <summary>
    ''' 最近的联机模块日志，用于出错时展示。
    ''' </summary>
    Private ReadOnly ModuleLogs As New Queue(Of String)

    ''' <summary>
    ''' 获取当前房间中的人数，包括自己。
    ''' 0 代表不可用。
    ''' </summary>
    Private ReadOnly Property PeopleCount As Integer
        Get
            Return ModulePeople
        End Get
    End Property

#Region "加载流程"

    Private WithEvents LinkLoader As New LoaderCombo(Of Integer)("联机", {
        New LoaderTask(Of Integer, Integer)("准备联机模块", AddressOf InitPrepareModule) With {.ProgressWeight = 20},
        New LoaderTask(Of Integer, Integer)("启动联机模块", AddressOf InitLaunch) With {.ProgressWeight = 80}
    })

    '1. 释放联机模块
    Private Sub InitPrepareModule(Task As LoaderTask(Of Integer, Integer))
        UpdateLoadingPage("正在准备联机模块……", "准备联机模块")
        Telemetry("联机开始")
        Try
            ExtractResources(PathLinkModule, "PCLink")
        Catch ex As Exception
            Throw New Exception("$释放联机模块失败：" & ex.Message)
        End Try
        If Not FileUtils.Exists(PathLinkModule) Then
            Throw New Exception($"$释放联机模块失败，请检查是否有杀毒软件拦截了 {PathLinkModule}。")
        End If
        Logger.Info($"联机模块已就绪：{PathLinkModule}")
        Task.Progress = 0.2
    End Sub

    '2. 启动联机模块并等待连接
    Private Sub InitLaunch(Task As LoaderTask(Of Integer, Integer))
        '准备启动参数
        Dim Arguments As String
        If IsServerSide Then
            Arguments = $"host --port {ServerPort}"
        Else
            UpdateLoadingPage("正在分配本地端口……", "分配本地端口")
            ClientPort = FindFreePorts(2, ServerPort).First
            Arguments = $"join --port {ClientPort} --host {HostSteamId}"
        End If
        '启动进程
        UpdateLoadingPage("正在启动联机模块……", "启动联机模块")
        ProcessStart(Arguments)
        Task.Progress = 0.3
        '等待连接
        Dim TimeoutCounter As Integer = 0
        Do
            Thread.Sleep(200)
            '检查状态
            CheckCrash()
            Dim LastError As String
            SyncLock ModuleLock
                LastError = ModuleError
            End SyncLock
            If Not String.IsNullOrEmpty(LastError) Then
                Panic(If(IsServerSide, "无法创建房间", "无法加入房间"), LastError)
                Throw New OperationCanceledException
            End If
            Dim Ready As Boolean = If(IsServerSide, Not String.IsNullOrEmpty(ModuleInvite), ModuleConnected)
            If Ready Then Exit Do
            '更新进度
            Task.Progress = (Task.Progress + 0.02).Clamp(0.3, 0.95)
            '超时判定
            If TimeoutCounter < 100 Then
                TimeoutCounter += 1
                If IsServerSide Then
                    UpdateLoadingPage("正在等待 Steam 网络就绪……", "等待 Steam 网络就绪")
                Else
                    UpdateLoadingPage("正在通过 Steam 连接房主……", "连接房主")
                End If
            Else '进度停滞超过 20s
                If IsServerSide Then
                    Panic("无法创建房间", $"可能的原因：{vbCrLf}- Steam 未启动或未登录{vbCrLf}- Steam 网络连接不稳定{vbCrLf}近期日志：{vbCrLf}{GetModuleLogs()}")
                Else
                    Panic("无法连接到房主", $"可能的原因：{vbCrLf}- 房间号输错了{vbCrLf}- 房主已关闭房间{vbCrLf}- 你或者房主的 Steam 未在线{vbCrLf}近期日志：{vbCrLf}{GetModuleLogs()}")
                End If
                Throw New OperationCanceledException
            End If
            If Task.IsCanceled Then Throw New OperationCanceledException
        Loop
        '完成
        If IsServerSide Then
            Telemetry("联机成功", "Server", True)
        Else
            ClientAddress = ModuleReady
            Telemetry("联机成功", "Server", False)
        End If
        Task.Progress = 1
    End Sub

    ''' <summary>
    ''' 获取最近的联机模块日志。
    ''' </summary>
    Private Function GetModuleLogs() As String
        SyncLock ModuleLock
            Return ModuleLogs.Join(vbCrLf)
        End SyncLock
    End Function

#End Region

#Region "进程与日志"

    ''' <summary>
    ''' 联机模块进程。
    ''' </summary>
    Private Shared ProcessCore As Process

    ''' <summary>
    ''' 若程序正在运行，则结束程序进程。
    ''' 即使失败也不会抛出异常。
    ''' </summary>
    Public Shared Sub ProcessStop()
        Try
            '关闭未捕获的进程
            For Each ProcessObject In Process.GetProcesses
                If ProcessObject.Id = ProcessCore?.Id Then Continue For
                If ProcessObject.ProcessName <> "PCLink" Then Continue For
                Try
                    Logger.Info($"停止残留的联机模块，PID：{ProcessObject.Id}")
                    ProcessObject.Kill()
                    ProcessObject.Close()
                Catch exx As Exception
                    Logger.Warn(exx, $"结束进程失败（{ProcessObject.ProcessName}，PID {ProcessObject.Id}）")
                End Try
            Next
            '关闭由自身启动的进程
            If ProcessCore IsNot Nothing AndAlso Not ProcessCore.HasExited Then
                Logger.Info($"停止所启动的联机模块，PID：{ProcessCore.Id}")
                ProcessCore.Kill()
                ProcessCore.Close()
            End If
            ProcessCore = Nothing
        Catch ex As Exception
            Logger.Warn(ex, "停止联机模块失败")
        End Try
    End Sub

    ''' <summary>
    ''' 出现意外错误，给出错误信息并结束联机。
    ''' </summary>
    Private Sub Panic(Brief As String, Detail As String)
        '显示信息
        If LinkState = LinkStates.Loading Then
            FailReason = Brief
            LinkLoader.Failed(New Exception(Detail))
        Else
            MyMsgBox(Detail, "联机出错：" & Brief, IsWarn:=True)
            ChangeState(LinkStates.Waiting)
            Telemetry("联机失败", "Exception", FilterUserName(Brief & "：" & Detail, "*"))
        End If
    End Sub

    ''' <summary>
    ''' 启动进程。
    ''' </summary>
    Private Sub ProcessStart(Arguments As String)
        Dim Info = New ProcessStartInfo With {
            .FileName = PathLinkModule,
            .Arguments = Arguments,
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardError = True,
            .RedirectStandardOutput = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        Logger.Info($"正在启动联机模块：{Arguments}")
        ProcessCore = New Process With {.StartInfo = Info}
        Dim LogLineHandler As DataReceivedEventHandler =
        Sub(sender As Object, e As DataReceivedEventArgs)
            Try
                If e.Data Is Nothing Then Return
                ProcessLogLine(e.Data)
            Catch unused As ObjectDisposedException
            Catch ex As Exception
                Logger.Warn(ex, "读取联机模块信息失败")
            End Try
        End Sub
        AddHandler ProcessCore.OutputDataReceived, LogLineHandler
        AddHandler ProcessCore.ErrorDataReceived, LogLineHandler
        ProcessCore.Start()
        ProcessCore.BeginOutputReadLine()
        ProcessCore.BeginErrorReadLine()
    End Sub

    ''' <summary>
    ''' 解析联机模块上报的一行信息，格式为 Key=Value。
    ''' </summary>
    Private Sub ProcessLogLine(Line As String)
        If String.IsNullOrWhiteSpace(Line) Then Return
        Dim Pos As Integer = Line.IndexOf("="c)
        If Pos <= 0 Then
            Logger.Info("[联机模块] " & Line)
            Return
        End If
        Dim Key As String = Line.Substring(0, Pos).Trim.ToUpperInvariant
        Dim Value As String = Line.Substring(Pos + 1).Trim
        Select Case Key
            Case "SELF"
                Logger.Info($"[联机模块] 自己的 Steam ID：{Value}")
            Case "LOBBY"
                SyncLock ModuleLock
                    ModuleInvite = Value
                End SyncLock
                Logger.Info($"[联机模块] 房间已创建：{Value}")
            Case "READY"
                SyncLock ModuleLock
                    ModuleReady = Value
                End SyncLock
                Logger.Info($"[联机模块] 本地游戏入口：{Value}")
            Case "JOINED"
                SyncLock ModuleLock
                    ModuleConnected = (Value = "1")
                End SyncLock
            Case "PEERS"
                Dim Count As Integer
                If Integer.TryParse(Value, Count) Then
                    SyncLock ModuleLock
                        ModulePeople = Count
                    End SyncLock
                End If
            Case "PING"
                Dim Ping As Double
                If Double.TryParse(Value, NumberStyles.Any, CultureInfo.InvariantCulture, Ping) Then
                    SyncLock ModuleLock
                        ModulePing = Ping
                    End SyncLock
                End If
            Case "STATE"
                UpdateLoadingState(Value)
            Case "ERROR"
                SyncLock ModuleLock
                    ModuleError = Value
                    ModuleLogs.Enqueue("[错误] " & Value)
                End SyncLock
                Logger.Warn("[联机模块] " & Value)
            Case "LOG"
                Logger.Info("[联机模块] " & Value)
                SyncLock ModuleLock
                    ModuleLogs.Enqueue(Value)
                    Do While ModuleLogs.Count > 10
                        ModuleLogs.Dequeue()
                    Loop
                End SyncLock
            Case Else
                Logger.Info("[联机模块] " & Line)
        End Select
    End Sub

    ''' <summary>
    ''' 检查联机模块是否崩溃。
    ''' </summary>
    Private Sub CheckCrash()
        If ProcessCore IsNot Nothing AndAlso Not ProcessCore.HasExited Then Return
        Panic("联机模块已崩溃", $"近期日志：{vbCrLf}{GetModuleLogs()}")
    End Sub

#End Region

#Region "定时任务"

    '启动
    Private Sub StartTimerThread() Handles Me.Loaded
        Static IsTimerStarted As Boolean = False
        If IsTimerStarted Then Return
        RunInNewThread(
        Sub()
            Dim Counter As Integer = 0
            Do While True
                Try
                    Thread.Sleep(200)
                    Counter += 200
                    '每秒执行一次
                    If Counter >= 1000 Then
                        Counter = 0
                        Update()
                    End If
                    '每 200ms 更新进度条
                    If LinkState = LinkStates.Loading Then RunInUi(AddressOf UpdateProgressBar)
                Catch ex As Exception
                    Logger.Error(ex, "联机模块主时钟出错")
                    Thread.Sleep(10000)
                End Try
            Loop
        End Sub, "Link Timer")
    End Sub

    '每秒或进入页面时触发
    Private Sub Update() Handles Me.Loaded
        If LinkState <> LinkStates.Finished Then Return
        '更新 UI
        If FrmMain.PageCurrent = FormMain.PageType.Link Then
            RunInUi(
            Sub()
                'Logo 旋转动画
                AniStart(AaRotateTransform(ImgFinishLogo, 500, 5000), "Link Logo Rotation")
                'Ping
                Dim Ping As Double = ModulePing
                If Ping <= 0 Then
                    LabFinishPing.Text = "连接中"
                Else
                    LabFinishPing.Text = If(Ping >= 10, $"{Ping:0} ms", $"{Ping:0.0} ms")
                End If
                '人数显示
                LabFinishPlayer.Text = ModulePeople & " 人"
                '提示条（Steam 会自动选择直连或中继，无需额外提示）
                HintFinish.Visibility = Visibility.Collapsed
            End Sub)
        End If
        '检查核心状态
        CheckCrash()
        '检查连接状态
        If Not ModuleConnected Then
            MyMsgBox("房主已离开房间，联机已断开！", "联机结束")
            ChangeState(LinkStates.Waiting)
            Return
        End If
        '广播联机房间端口
        If Not IsServerSide Then
            Try
                Static BroadcastSocket As New Socket(SocketType.Dgram, ProtocolType.Udp)
                BroadcastSocket.SendTo(
                    Encoding.UTF8.GetBytes($"[MOTD]PCL 联机房间[/MOTD][AD]{ClientPort}[/AD]"),
                    SocketFlags.None,
                    New IPEndPoint(IPAddress.Loopback, 4445))
            Catch ex As Exception
                Logger.Warn(ex, "广播联机房间端口失败")
            End Try
        End If
    End Sub

#End Region

End Class
