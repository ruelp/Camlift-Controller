﻿Imports VisionaryDigital.CanonCamera.Sdk
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Drawing
Imports System.IO

#Const USE_FAKE_CAMERA = False

Public Enum CameraState
    Ready
    TooManyCameras
    NoCameraConnected
    DeviceBusy
End Enum

#If Not USE_FAKE_CAMERA Then
Public Class Camera
    Implements IDisposable

    Public Const CameraName_5D As String = "Canon EOS 5D Mark II"
    Public Const CameraName_40D As String = "Canon EOS 40D"
    Public Const CameraName_7D As String = "Canon EOS 7D"

    Public Structure CameraModelData
        Public Zoom100MaxPosition As Point
        Public Zoom500MaxPosition As Point
        Public Zoom100ImageSize As Size
        Public Zoom500ImageSize As Size
        Public ZoomBoxSize As Size
    End Structure
    Private m_modelData As Dictionary(Of String, CameraModelData)

    Private m_cam As IntPtr
    Private m_oeh As EdsObjectEventHandler
    Private m_seh As EdsStateEventHandler
    Private m_peh As EdsPropertyEventHandler

    Private Shared s_instance As Camera = Nothing

    Private m_waitingOnPic As Boolean
    Private m_picOutFolder As String

    ' live view
    Private Const LiveViewDelay = 200
    Private Const LiveViewFrameBufferSize = &H800000
    Private m_liveViewThread As Thread
    Private m_liveViewOn As Boolean
    Private m_waitingToStartLiveView As Boolean
    Private m_liveViewPicBox As PictureBox
    Private m_stoppingLiveView As Boolean
    Private m_liveViewFrameBuffer As Byte()
    Private m_liveViewBufferHandle As GCHandle
    Private m_liveViewStreamPtr As IntPtr
    Private m_liveViewImageSize As Size
    Private m_rotation As Drawing.RotateFlipType

    Private m_zoomPosition As StructurePointer(Of EdsPoint)
    Private m_pendingZoomPosition As Boolean
    Private m_pendingZoomPoint As StructurePointer(Of EdsPoint)
    Private m_zoomRatio As StructurePointer(Of Integer)
    Private m_pendingZoomRatio As Boolean
    Private m_whiteBalance As StructurePointer(Of Integer)
    Private m_pendingWhiteBalance As Boolean
    Private m_fastPictures As Boolean

    Private m_haveSession As Boolean

    Private m_fastPicturesInteruptingLiveView As Boolean
    Private m_fastPicturesLiveViewBox As PictureBox

    Private m_disposed As Boolean

    Private Const SleepTimeout = 10000 ' how many milliseconds to wait before giving up
    Private Const SleepAmount = 50 ' how many milliseconds to sleep before doing the event pump

    Private Const ForceJpeg = False

    Private m_transferQueue As Queue(Of TransferItem)

    Private Declare Function CoInitializeEx Lib "OLE32" (ByVal pvReserved As IntPtr, ByVal dwCoInit As UInteger) As Integer

    Public Sub New()
        If s_instance Is Nothing Then
            s_instance = Me
            m_disposed = False
            ResetState()

            CheckError(EdsInitializeSDK())

            While True
                Try
                    EstablishSession()
                    Exit While
                Catch ex As Exception
                    If HandleCameraException(ex) Then Continue While
                    Throw New GtfoException ' Exit program
                End Try
            End While
        Else
            Throw New OnlyOneInstanceAllowedException
        End If
    End Sub

    Public ReadOnly Property CameraSpecificData() As CameraModelData
        Get
            Dim myName As String = Me.Name()
            If m_modelData.ContainsKey(myName) Then
                Return m_modelData.Item(myName)
            Else
                Return m_modelData.Item(CameraName_40D)
            End If
        End Get
    End Property

    Private Sub ResetState()
        'camera specific information
        m_modelData = New Dictionary(Of String, CameraModelData)
        '40D
        Dim data40D As CameraModelData
        data40D.Zoom100ImageSize = New Size(1024, 680)
        data40D.Zoom500ImageSize = New Size(768, 800)
        data40D.Zoom100MaxPosition = New Point(3104, 2016)
        data40D.Zoom500MaxPosition = New Point(3104, 2080)
        data40D.ZoomBoxSize = New Size(204, 208)

        '5D
        Dim data5D As CameraModelData
        data5D.Zoom100ImageSize = New Size(1024, 680)
        data5D.Zoom500ImageSize = New Size(1120, 752)
        data5D.Zoom100MaxPosition = New Point(4464, 2976)
        data5D.Zoom500MaxPosition = New Point(4464, 2976)
        data5D.ZoomBoxSize = New Size(202, 135)

        '7D
        Dim data7D As CameraModelData
        data7D.Zoom100ImageSize = New Size(1056, 704)
        data7D.Zoom500ImageSize = New Size(1024, 680)
        data7D.Zoom100MaxPosition = New Point(4136, 2754)
        data7D.Zoom500MaxPosition = New Point(4136, 2754)
        data7D.ZoomBoxSize = New Size(212, 144)

        m_modelData.Add(CameraName_40D, data40D)
        m_modelData.Add(CameraName_5D, data5D)
        m_modelData.Add(CameraName_7D, data7D)

        m_waitingOnPic = False
        m_liveViewOn = False
        m_waitingToStartLiveView = False
        m_liveViewPicBox = Nothing
        m_liveViewThread = Nothing
        m_stoppingLiveView = False
        ReDim m_liveViewFrameBuffer(0)
        m_liveViewBufferHandle = Nothing
        m_liveViewStreamPtr = IntPtr.Zero
        m_liveViewImageSize = Nothing

        m_transferQueue = New Queue(Of TransferItem)
        m_haveSession = False

        m_zoomPosition = New StructurePointer(Of EdsPoint)
        m_pendingZoomPoint = New StructurePointer(Of EdsPoint)
        m_zoomRatio = New StructurePointer(Of Integer)
        m_whiteBalance = New StructurePointer(Of Integer)

        m_pendingZoomRatio = False
        m_pendingZoomPosition = False
        m_pendingWhiteBalance = False

        m_fastPictures = False

        m_fastPicturesInteruptingLiveView = False
        m_fastPicturesLiveViewBox = Nothing
        m_rotation = RotateFlipType.RotateNoneFlipNone
    End Sub

    Private Sub EstablishSession()
        Dim camList As IntPtr
        Dim numCams As Integer

        If m_haveSession Then Exit Sub

        CheckError(EdsGetCameraList(camList))
        CheckError(EdsGetChildCount(camList, numCams))

        If numCams > 1 Then
            CheckError(EdsRelease(camList))
            Throw New TooManyCamerasFoundException
        ElseIf numCams = 0 Then
            CheckError(EdsRelease(camList))
            Throw New NoCameraFoundException
        End If

        'get the only camera
        CheckError(EdsGetChildAtIndex(camList, 0, m_cam))

        'release the camera list data
        CheckError(EdsRelease(camList))

        'open a session
        CheckError(EdsOpenSession(m_cam))

        ' handlers
        m_seh = New EdsStateEventHandler(AddressOf StaticStateEventHandler)
        CheckError(EdsSetCameraStateEventHandler(m_cam, kEdsStateEvent_All, m_seh, New IntPtr(0)))

        m_oeh = New EdsObjectEventHandler(AddressOf StaticObjectEventHandler)
        CheckError(EdsSetObjectEventHandler(m_cam, kEdsObjectEvent_All, m_oeh, New IntPtr(0)))

        m_peh = New EdsPropertyEventHandler(AddressOf StaticPropertyEventHandler)
        CheckError(EdsSetPropertyEventHandler(m_cam, kEdsPropertyEvent_All, m_peh, New IntPtr(0)))

        'set default options
        'save to computer, not memory card
        CheckError(EdsSetPropertyData(m_cam, kEdsPropID_SaveTo, 0, Marshal.SizeOf(GetType(Integer)), CType(EdsSaveTo.kEdsSaveTo_Host, Integer)))

        If ForceJpeg Then
            ' enforce JPEG format
            Dim qs As New StructurePointer(Of UInt32)
            CheckError(EdsGetPropertyData(m_cam, kEdsPropID_ImageQuality, 0, qs.Size, qs.Pointer))
            ' clear the old image type setting and set the new one
            qs.Value = qs.Value And &HFF0FFFFFL Or (EdsImageType.kEdsImageType_Jpeg << 20)
            CheckError(EdsSetPropertyData(m_cam, kEdsPropID_ImageQuality, 0, qs.Size, qs.Value))
        End If

        m_haveSession = True
    End Sub


    Private Sub ReleaseSession()
        'Debug.Assert(m_fastPictures = False)
        'EdsCloseSession(m_cam)
        'EdsRelease(m_cam)
        'm_haveSession = False
    End Sub

    Public Sub Dispose() Implements System.IDisposable.Dispose
        If m_disposed Then Exit Sub
        m_disposed = True
        If Not m_haveSession Then Exit Sub
        StopLiveView() 'stops it only if it's running

        'FlushTransferQueue()
        'ReleaseSession()
        'EdsTerminateSDK()

        s_instance = Nothing
    End Sub

    Private Sub CheckError(ByVal Err As Integer)
        ' check for special errors
        Select Case Err
            Case EDS_ERR_INVALID_HANDLE
                ' camera was disconnected.
                ' clean up
                ResetState()
                EdsCloseSession(m_cam)
                EdsRelease(m_cam)

                Throw New CameraDisconnectedException
            Case EDS_ERR_COMM_PORT_IS_IN_USE
                'EOSUtility got to it before we did.
                ResetState()
                EdsCloseSession(m_cam)
                EdsRelease(m_cam)
        End Select
        ' throw errors if necessary
        If Err <> EDS_ERR_OK Then Throw New SdkException(Err)
    End Sub

    Private Shared Function StaticObjectEventHandler(ByVal inEvent As Integer, ByVal inRef As IntPtr, ByVal inContext As IntPtr) As Long
        'transfer from static to member
        s_instance.ObjectEventHandler(inEvent, inRef, inContext)
        Return 0
    End Function

    Private Shared Function StaticStateEventHandler(ByVal inEvent As Integer, ByVal inParameter As Integer, ByVal inContext As IntPtr) As Long
        'transfer from static to member
        s_instance.StateEventHandler(inEvent, inParameter, inContext)
        Return 0
    End Function

    Private Shared Function StaticPropertyEventHandler(ByVal inEvent As Integer, ByVal inPropertyID As Integer, ByVal inParam As Integer, ByVal inContext As IntPtr) As Long
        'transfer from static to member
        s_instance.PropertyEventHandler(inEvent, inPropertyID, inParam, inContext)
        Return 0
    End Function

    Private Sub ObjectEventHandler(ByVal inEvent As Integer, ByVal inRef As IntPtr, ByVal inContext As IntPtr)
        Select Case inEvent
            Case kEdsObjectEvent_DirItemRequestTransfer
                If m_fastPictures Then
                    ' queue up the transfer request
                    Dim transfer As New TransferItem
                    transfer.sdkRef = inRef
                    transfer.outFile = m_picOutFolder
                    m_transferQueue.Enqueue(transfer)
                Else
                    TransferOneItem(inRef, m_picOutFolder)
                End If

                m_waitingOnPic = False ' allow other thread to continue
            Case Else
                Debug.Print(String.Format("ObjectEventHandler: event {0}", inEvent))
        End Select
    End Sub

    Private Function EnsureDoesNotExist(ByVal outfile As String) As String
        If File.Exists(outfile) Then
            Dim title As String = Path.GetFileNameWithoutExtension(outfile)
            Dim folder As String = Path.GetDirectoryName(outfile)
            Dim ext As String = Path.GetExtension(outfile)
            Dim append As Integer = 1
            While File.Exists(Path.Combine(folder, title & "_" & append & ext))
                append += 1
            End While
            Return Path.Combine(folder, title & "_" & append & ext)
        Else
            Return outfile
        End If
    End Function

    Private Sub TransferOneItem(ByVal inRef As IntPtr, ByVal outFolder As String)
        ' transfer the image in memory to disk
        Dim dirItemInfo As EdsDirectoryItemInfo = Nothing
        Dim outStream As IntPtr

        CheckError(EdsGetDirectoryItemInfo(inRef, dirItemInfo))


        Dim outfile As String = Path.Combine(outFolder, dirItemInfo.szFileName)

        ' make sure we don't overwrite files
        outfile = EnsureDoesNotExist(outfile)

        ' get a temp file to write to
        Dim tmpfile As String = My.Computer.FileSystem.GetTempFileName()

        ' This creates the outStream that is used by EdsDownload to actually grab and write out the file.
        CheckError(EdsCreateFileStream(tmpfile, EdsFileCreateDisposition.kEdsFileCreateDisposition_CreateAlways, EdsAccess.kEdsAccess_ReadWrite, outStream))

        ' do the transfer
        CheckError(EdsDownload(inRef, dirItemInfo.size, outStream))
        CheckError(EdsDownloadComplete(inRef))

        '' manipulate the image
        'Dim imgRef As IntPtr
        'CheckError(EdsCreateImageRef(outStream, imgRef))

        '' always landscape
        'Dim orientation As UInt32 = 6
        'CheckError(EdsSetPropertyData(imgRef, kEdsPropID_Orientation, orientation, Marshal.SizeOf(orientation), orientation))
        'Dim qs As New StructurePointer(Of UInt32)
        'CheckError(EdsGetPropertyData(imgRef, kEdsPropID_ImageQuality, 0, qs.Size, qs.Pointer))
        'Dim saveSettings As EdsSaveImageSetting
        'saveSettings.iccProfileStream = 0
        'saveSettings.JPEGQuality = qs.Value
        'saveSettings.reserved = 0
        'Dim saveOutstream As IntPtr
        'CheckError(EdsCreateFileStream(outfile, EdsFileCreateDisposition.kEdsFileCreateDisposition_CreateAlways, EdsAccess.kEdsAccess_Write, saveOutstream))

        'CheckError(EdsSaveImage(imgRef, EdsTargetImageType.kEdsTargetImageType_Jpeg, saveSettings, saveOutstream))
        'CheckError(EdsRelease(imgRef))
        'CheckError(EdsRelease(saveOutstream))


        CheckError(EdsRelease(outStream))

        FileCopy(tmpfile, outfile)
    End Sub

    Public Sub EndFastPictures()
        If Not m_fastPictures Then Exit Sub

        While m_transferQueue.Count > 0
            Dim transfer As TransferItem = m_transferQueue.Dequeue()
            TransferOneItem(transfer.sdkRef, transfer.outFile)
        End While
        m_fastPictures = False

        ReleaseSession()

        If m_fastPicturesInteruptingLiveView Then
            m_fastPicturesInteruptingLiveView = False
            StartLiveView(m_fastPicturesLiveViewBox)
        End If
    End Sub

    Public Sub BeginFastPictures()
        CheckBusy()

        m_fastPicturesInteruptingLiveView = m_liveViewOn
        m_fastPicturesLiveViewBox = m_liveViewPicBox
        If m_fastPicturesInteruptingLiveView Then StopLiveView()

        EstablishSession()
        m_fastPictures = True
    End Sub

    Private Sub StateEventHandler(ByVal inEvent As Integer, ByVal inParameter As Integer, ByVal inContext As IntPtr)
        Debug.Print(String.Format("stateEventHandler: event {0}, parameter {1}", inEvent, inParameter))
    End Sub

    Private Sub PropertyEventHandler(ByVal inEvent As Integer, ByVal inPropertyID As Integer, ByVal inParam As Integer, ByVal inContext As IntPtr)
        Select Case inPropertyID
            Case kEdsPropID_Evf_OutputDevice
                If m_waitingToStartLiveView Then
                    'start live view thread
                    m_liveViewThread = New Thread(AddressOf UpdateLiveView)
                    m_liveViewThread.Start()

                    'save state
                    m_waitingToStartLiveView = False
                    m_liveViewOn = True
                End If
            Case Else
                Debug.Print("property event handler called, propid = " & inPropertyID)
        End Select

    End Sub

    Private Sub CheckBusy()
        If m_waitingOnPic Or m_waitingToStartLiveView Then
            ' bad programmer. should have disabled user controls
            Throw New CameraIsBusyException
        End If
    End Sub

    ''' <summary>
    ''' Take a fast picture. Needs to be preceeded by a call to BeginFastPictures() and closed with a call
    ''' to EndFastPictures(). Drops the picture in outFolder.
    ''' </summary>
    ''' <param name="outFolder">folder to save the picture</param>
    ''' <remarks></remarks>
    Public Sub TakeFastPicture(ByVal outFolder As String)
        If Not m_fastPictures Then Throw New CameraIsBusyException

        CheckDirectory(outFolder)

        ' set flag indicating we are waiting on a callback call
        m_waitingOnPic = True
        m_picOutFolder = outFolder

        Dim TryAgain As Boolean = True
        While TryAgain
            TryAgain = False
            Try
                TakePicture(outFolder)
            Catch ex As SdkException When ex.SdkError = SdkErrors.DeviceBusy
                Thread.Sleep(SleepAmount)
                TryAgain = True
            End Try
        End While
    End Sub

    Private Sub CheckDirectory(ByVal Path As String)
        If Not Directory.Exists(Path) Then Throw New DirectoryDoesNotExistException
    End Sub

    '''<summary>snap a photo with the camera and drop in OutFolder</summary> 
    ''' <param name="OutFolder">the folder to save the picture in</param>
    Public Sub TakeSinglePicture(ByVal OutFolder As String)
        Dim interuptingLiveView As Boolean = m_liveViewOn
        Dim lvBox As PictureBox = m_liveViewPicBox

        Dim haveSession As Boolean = m_haveSession

        CheckDirectory(OutFolder)

        EstablishSession()
        CheckBusy()

        If interuptingLiveView Then StopLiveView()


        ' set flag indicating we are waiting on a callback call
        m_waitingOnPic = True
        m_picOutFolder = OutFolder

        If TakePicture(OutFolder) Then
            If interuptingLiveView Then StartLiveView(lvBox)
        Else
            ' we never got a callback. throw an error
            If interuptingLiveView Then
                StartLiveView(lvBox)
            Else
                If Not haveSession Then ReleaseSession()
            End If


            m_waitingOnPic = False
            Throw New TakePictureFailedException
        End If
    End Sub

    Private Sub LieToTheCameraAboutHowMuchSpaceWeHaveOnTheComputer()
        ' tell the camera how much disk space we have left
        Dim caps As EdsCapacity

        caps.reset = True
        caps.bytesPerSector = 512
        caps.numberOfFreeClusters = Marshal.SizeOf(GetType(Integer)) ' arbitrary large number
        CheckError(EdsSetCapacity(m_cam, caps))

    End Sub

    ' internal takepicture function
    Private Function TakePicture(ByVal OutFile As String) As Boolean
        LieToTheCameraAboutHowMuchSpaceWeHaveOnTheComputer()

        ' take a picture with the camera and save it to outfile
        Dim err As Integer = EdsSendCommand(m_cam, EdsCameraCommand.kEdsCameraCommand_TakePicture, 0)

        If err <> EDS_ERR_OK Then
            m_waitingOnPic = False
            CheckError(err)
        End If

        Dim I As Integer
        For I = 0 To SleepTimeout / SleepAmount
            System.Threading.Thread.Sleep(SleepAmount)
            Application.DoEvents()

            If Not m_waitingOnPic Then Return True ' success
        Next I

        Return False
    End Function

    Private Sub StartBulb()
        Dim err As Integer

        CheckError(EdsSendStatusCommand(m_cam, EdsCameraStatusCommand.kEdsCameraStatusCommand_UILock, 0))

        err = EdsSendCommand(m_cam, EdsCameraCommand.kEdsCameraCommand_BulbStart, 0)

        ' call ui unlock if bulbstart fails
        If err <> EDS_ERR_OK Then
            EdsSendStatusCommand(m_cam, EdsCameraStatusCommand.kEdsCameraStatusCommand_UIUnLock, 0)
            CheckError(err)
        End If
    End Sub

    Private Sub StopBulb()
        Dim err As Integer, err2 As Integer

        ' call ui unlock even if bulb end fails
        err = EdsSendCommand(m_cam, EdsCameraCommand.kEdsCameraCommand_BulbEnd, 0)
        err2 = EdsSendCommand(m_cam, EdsCameraStatusCommand.kEdsCameraStatusCommand_UIUnLock, 0)

        CheckError(err)
        CheckError(err2)
    End Sub

    Public Property Rotation() As Drawing.RotateFlipType
        Get
            Return m_rotation
        End Get
        Set(ByVal value As Drawing.RotateFlipType)
            m_rotation = value
        End Set
    End Property

    '''<summary>start streaming live video to pbox</summary>
    '''<param name="pbox">the picture box to send live video to</param>
    ''' <remarks>you can only have one live view going at a time.</remarks>
    Public Sub StartLiveView(ByVal pbox As PictureBox)
        EstablishSession()

        While m_stoppingLiveView
            Application.DoEvents()
        End While

        If m_waitingToStartLiveView Then
            m_liveViewPicBox = pbox
            Return
        ElseIf m_liveViewOn Then
            StopLiveView()
        End If

        Dim device As New StructurePointer(Of UInt32)

        ' tell the camera to send live data to the computer
        CheckError(EdsGetPropertyData(m_cam, kEdsPropID_Evf_OutputDevice, 0, device.Size, device.Pointer))
        device.Value = device.Value Or EdsEvfOutputDevice.kEdsEvfOutputDevice_PC
        CheckError(EdsSetPropertyData(m_cam, kEdsPropID_Evf_OutputDevice, 0, device.Size, device.Value))

        ' get ready to stream
        m_liveViewPicBox = pbox
        m_waitingToStartLiveView = True

        ' set up buffer
        ReDim m_liveViewFrameBuffer(LiveViewFrameBufferSize)
        m_liveViewBufferHandle = GCHandle.Alloc(m_liveViewFrameBuffer, GCHandleType.Pinned)
        CheckError(EdsCreateMemoryStreamFromPointer(m_liveViewBufferHandle.AddrOfPinnedObject, LiveViewFrameBufferSize, m_liveViewStreamPtr))

        ' pause this thread until live view starts
        For i = 0 To SleepTimeout / SleepAmount
            System.Threading.Thread.Sleep(SleepAmount)
            Application.DoEvents()

            If Not m_waitingToStartLiveView Then
                'success, restore zoom settings
                ZoomPosition = New Point(m_zoomPosition.Value.x, m_zoomPosition.Value.y)
                ZoomRatio = m_zoomRatio.Value
                Exit Sub
            End If
        Next i

        ' we never got a callback. throw an error
        StopLiveView()
        Throw New LiveViewFailedException

    End Sub

    ''' <summary>
    ''' stop streaming live video
    ''' </summary>
    ''' <remarks></remarks>
    Public Sub StopLiveView()
        Dim device As New StructurePointer(Of UInt32)

        Dim haveSession As Boolean = m_haveSession

        EstablishSession()

        If m_stoppingLiveView Or (Not m_waitingToStartLiveView And Not m_liveViewOn) Then Exit Sub

        If m_liveViewOn Then
            ' stop thread
            m_stoppingLiveView = True
            m_liveViewThread.Join()
        End If

        ' save state
        m_liveViewOn = False
        m_waitingToStartLiveView = False
        m_liveViewPicBox = Nothing
        m_stoppingLiveView = False
        m_liveViewImageSize = Nothing

        ' tell the camera not to send live data to the computer
        CheckError(EdsGetPropertyData(m_cam, kEdsPropID_Evf_OutputDevice, 0, device.Size, device.Pointer))
        device.Value = device.Value And Not EdsEvfOutputDevice.kEdsEvfOutputDevice_PC
        CheckError(EdsSetPropertyData(m_cam, kEdsPropID_Evf_OutputDevice, 0, device.Size, device.Value))

        ' clean up
        CheckError(EdsRelease(m_liveViewStreamPtr))
        m_liveViewBufferHandle.Free()

        If Not haveSession Then ReleaseSession()
    End Sub

    Private Sub UpdateLiveView()
        Dim nowPlusInterval As Long

        CoInitializeEx(0, 2)
        While Not m_stoppingLiveView
            nowPlusInterval = Now.Ticks + LiveViewDelay
            Try
                ShowLiveViewFrame()
            Catch ex As SdkException When ex.SdkError = SdkErrors.StreamWriteError
                'that sucks. oh well.
            End Try
            Thread.Sleep(Math.Max(nowPlusInterval - Now.Ticks, 0))
        End While
    End Sub

    Private Sub ShowLiveViewFrame()
        Dim err As Integer = 0
        Dim imagePtr As IntPtr

        ' create image
        CheckError(EdsCreateEvfImageRef(m_liveViewStreamPtr, imagePtr))

        ' download the frame
        Try
            CheckError(EdsDownloadEvfImage(m_cam, imagePtr))
        Catch ex As SdkException When ex.SdkError = SdkErrors.ObjectNotready
            CheckError(EdsRelease(imagePtr))
            Exit Sub
        End Try
        Dim oldZoomRatio As Integer = m_zoomRatio.Value
        Dim oldZoomPoint As EdsPoint = m_pendingZoomPoint.Value

        ' get incidental data
        ' zoom ratio
        CheckError(EdsGetPropertyData(imagePtr, kEdsPropID_Evf_Zoom, 0, m_zoomRatio.Size, m_zoomRatio.Pointer))
        ' zoom position
        CheckError(EdsGetPropertyData(imagePtr, kEdsPropID_Evf_ZoomPosition, 0, m_zoomPosition.Size, m_zoomPosition.Pointer))

        ' set camera data
        If m_pendingZoomRatio Then
            m_zoomRatio.Value = oldZoomRatio
            CheckError(EdsSetPropertyData(m_cam, kEdsPropID_Evf_Zoom, 0, m_zoomRatio.Size, m_zoomRatio.Value))
            m_pendingZoomRatio = False
        End If
        If m_pendingZoomPosition Then
            m_pendingZoomPoint.Value = oldZoomPoint
            CheckError(EdsSetPropertyData(m_cam, kEdsPropID_Evf_ZoomPosition, 0, m_pendingZoomPoint.Size, m_pendingZoomPoint.Value))
            m_pendingZoomPosition = False
        End If
        If m_pendingWhiteBalance Then
            CheckError(EdsSetPropertyData(m_cam, kEdsPropID_Evf_WhiteBalance, 0, m_whiteBalance.Size, m_whiteBalance.Value))
            m_pendingWhiteBalance = False
        End If



        ' get it into the picture box image
        Dim canonImg As Image = Image.FromStream(New MemoryStream(m_liveViewFrameBuffer)) 'do not dispose the MemoryStream (Image.FromStream)
        canonImg.RotateFlip(m_rotation)
        m_liveViewImageSize = canonImg.Size
        Dim oldImg As Image = m_liveViewPicBox.Image
        m_liveViewPicBox.Image = canonImg
        If oldImg IsNot Nothing Then oldImg.Dispose() 'really is required.

        ' release image
        CheckError(EdsRelease(imagePtr))

    End Sub

    'Protected Overrides Sub Finalize()
    '    Dispose()
    '    MyBase.Finalize()
    'End Sub

    ''' <summary>
    ''' size of the frames coming through live view. only valid once live view has started.
    ''' </summary>
    ''' <value></value>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Public ReadOnly Property LiveViewImageSize() As Size
        Get
            Return m_liveViewImageSize
        End Get
    End Property

    Public Property ZoomPosition() As Point
        Get
            Return New Point(m_zoomPosition.Value.x, m_zoomPosition.Value.y)
        End Get
        Set(ByVal value As Point)
            m_pendingZoomPoint.Value = New EdsPoint() With {.x = value.X, .y = value.Y}
            m_pendingZoomPosition = True
        End Set
    End Property


    ''' <summary>
    ''' Get the zoom factor of live view. Returns an integer which is a multiplier.
    ''' </summary>
    ''' <value></value>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Public Property ZoomRatio() As Integer
        Get
            Return m_zoomRatio.Value
        End Get
        Set(ByVal value As Integer)
            m_zoomRatio.Value = value
            m_pendingZoomRatio = True
        End Set
    End Property

    Public Property WhiteBalance() As Integer
        Get
            Dim haveSession As Boolean = m_haveSession
            EstablishSession()
            CheckError(EdsGetPropertyData(m_cam, kEdsPropID_WhiteBalance, 0, m_whiteBalance.Size, m_whiteBalance.Pointer))
            If Not haveSession Then ReleaseSession()
            Return m_whiteBalance.Value
        End Get
        Set(ByVal value As Integer)
            m_whiteBalance.Value = value
            m_pendingWhiteBalance = True
        End Set
    End Property

    Public ReadOnly Property Name() As String
        Get
            Dim deviceInfo As New EdsDeviceInfo
            CheckError(EdsGetDeviceInfo(m_cam, deviceInfo))
            Return deviceInfo.szDeviceDescription
        End Get
    End Property

    Private Class StructurePointer(Of T As Structure)

        Private m_Size As Integer 'in bytes
        Private m_ptr As IntPtr

        Public ReadOnly Property Size() As Integer
            Get
                Return m_Size
            End Get
        End Property

        Public ReadOnly Property Pointer() As IntPtr
            Get
                Return m_ptr
            End Get
        End Property

        Public Property Value() As T
            Get
                Return Marshal.PtrToStructure(m_ptr, GetType(T))
            End Get
            Set(ByVal value As T)
                Marshal.StructureToPtr(value, m_ptr, True)
            End Set
        End Property

        Public Sub New()
            m_Size = Marshal.SizeOf(GetType(T))
            m_ptr = Marshal.AllocHGlobal(m_Size)
        End Sub

        Protected Overrides Sub Finalize()
            Marshal.FreeHGlobal(m_ptr)
        End Sub
    End Class

    Private Structure TransferItem
        Public sdkRef As IntPtr
        Public outFile As String
    End Structure

End Class

#Else 'use fake camera
Public Class Camera
    Implements IDisposable

    Public Structure CameraModelData
        Public Zoom100MaxPosition As Point
        Public Zoom500MaxPosition As Point
        Public Zoom100ImageSize As Size
        Public Zoom500ImageSize As Size
        Public ZoomBoxSize As Size
    End Structure


    Public Const CameraName_5D As String = "Canon EOS 5D Mark II"
    Public Const CameraName_40D As String = "Canon EOS 40D"
    Public Const CameraName_7D As String = "Canon EOS 7D"


    Private m_zoomRatio As Integer
    Private m_zoomPosition As Point
    Private m_whiteBalance As Integer
    Private m_rotation As Drawing.RotateFlipType = RotateFlipType.RotateNoneFlipNone

    Public Sub Dispose() Implements IDisposable.Dispose
    End Sub



    Public ReadOnly Property CameraSpecificData() As CameraModelData
        Get
            Return New CameraModelData
        End Get
    End Property

    Public Sub TakeSinglePicture(ByVal outfile As String)
        Debug.Print("oh snap: " & outfile)
    End Sub

    Public sub TakeFastPicture(outfile As String)
        TakeSinglePicture(outfile)
    end sub

    Public Sub StartLiveView(ByVal pbox As PictureBox)

    End Sub

    Public Sub StopLiveView()

    End Sub

    Public Sub EstablishSession()

    End Sub

    Public Sub BeginFastPictures()

    End Sub

    Public sub EndFastPictures()

    End Sub

    Public Property ZoomRatio() As Integer
        Get
            Return m_zoomRatio
        End Get
        Set(ByVal value As Integer)
            m_zoomRatio = value
        End Set
    End Property

    Public Property ZoomPosition() As Point
        Get
            Return m_zoomPosition
        End Get
        Set(ByVal value As Point)
            m_zoomPosition = value
        End Set
    End Property

    Public Property WhiteBalance() As Integer
        Get
            Return m_whiteBalance
        End Get
        Set(ByVal value As Integer)
            m_whiteBalance = value
        End Set
    End Property

    Public Sub New()
        m_zoomPosition = New Point(0, 0)
        m_zoomRatio = 1
    End Sub

    Public ReadOnly Property Name() As String
        Get
            Return CameraName_40D
        End Get
    End Property

    Public ReadOnly Property LiveViewImageSize() As Size
        Get
            Return New Size(640, 480)
        End Get
    End Property

    Public Property Rotation() As Drawing.RotateFlipType
        Get
            Return m_rotation
        End Get
        Set(ByVal value As Drawing.RotateFlipType)
            m_rotation = value
        End Set
    End Property
End Class
#End If


Public Class SdkException
    Inherits Exception

    Private m_Message As String

    Public ReadOnly Property SdkError() As String
        Get
            Return m_Message
        End Get
    End Property

    Public Sub New(ByVal errCode As Integer)
        m_Message = SdkErrors.StringFromErrorCode(errCode)
    End Sub

    Public Overrides ReadOnly Property Message() As String
        Get
            Return m_Message
        End Get
    End Property
End Class

Public NotInheritable Class SdkErrors
    Private Sub New() 'static class
    End Sub

    Private Shared m_dict As Dictionary(Of Integer, String)

    Public Shared Function StringFromErrorCode(ByVal errCode As Integer) As String
        If m_dict Is Nothing Then initDict()
        If m_dict.ContainsKey(errCode) Then
            Return m_dict.Item(errCode)
        Else
            Return "Error code: " & errCode
        End If
    End Function

#Region "Generated Code"

    Private Shared Sub initDict()
        m_dict = New Dictionary(Of Integer, String)(117)

        ' Miscellaneous errors
        m_dict.Add(EDS_ERR_UNIMPLEMENTED, Unimplemented)
        m_dict.Add(EDS_ERR_INTERNAL_ERROR, InternalError)
        m_dict.Add(EDS_ERR_MEM_ALLOC_FAILED, MemAllocFailed)
        m_dict.Add(EDS_ERR_MEM_FREE_FAILED, MemFreeFailed)
        m_dict.Add(EDS_ERR_OPERATION_CANCELLED, OperationCancelled)
        m_dict.Add(EDS_ERR_INCOMPATIBLE_VERSION, IncompatibleVersion)
        m_dict.Add(EDS_ERR_NOT_SUPPORTED, NotSupported)
        m_dict.Add(EDS_ERR_UNEXPECTED_EXCEPTION, UnexpectedException)
        m_dict.Add(EDS_ERR_PROTECTION_VIOLATION, ProtectionViolation)
        m_dict.Add(EDS_ERR_MISSING_SUBCOMPONENT, MissingSubcomponent)
        m_dict.Add(EDS_ERR_SELECTION_UNAVAILABLE, SelectionUnavailable)

        ' File errors
        m_dict.Add(EDS_ERR_FILE_IO_ERROR, FileIoError)
        m_dict.Add(EDS_ERR_FILE_TOO_MANY_OPEN, FileTooManyOpen)
        m_dict.Add(EDS_ERR_FILE_NOT_FOUND, FileNotFound)
        m_dict.Add(EDS_ERR_FILE_OPEN_ERROR, FileOpenError)
        m_dict.Add(EDS_ERR_FILE_CLOSE_ERROR, FileCloseError)
        m_dict.Add(EDS_ERR_FILE_SEEK_ERROR, FileSeekError)
        m_dict.Add(EDS_ERR_FILE_TELL_ERROR, FileTellError)
        m_dict.Add(EDS_ERR_FILE_READ_ERROR, FileReadError)
        m_dict.Add(EDS_ERR_FILE_WRITE_ERROR, FileWriteError)
        m_dict.Add(EDS_ERR_FILE_PERMISSION_ERROR, FilePermissionError)
        m_dict.Add(EDS_ERR_FILE_DISK_FULL_ERROR, FileDiskFullError)
        m_dict.Add(EDS_ERR_FILE_ALREADY_EXISTS, FileAlreadyExists)
        m_dict.Add(EDS_ERR_FILE_FORMAT_UNRECOGNIZED, FileFormatUnrecognized)
        m_dict.Add(EDS_ERR_FILE_DATA_CORRUPT, FileDataCorrupt)
        m_dict.Add(EDS_ERR_FILE_NAMING_NA, FileNamingNa)

        ' Directory errors
        m_dict.Add(EDS_ERR_DIR_NOT_FOUND, DirNotFound)
        m_dict.Add(EDS_ERR_DIR_IO_ERROR, DirIoError)
        m_dict.Add(EDS_ERR_DIR_ENTRY_NOT_FOUND, DirEntryNotFound)
        m_dict.Add(EDS_ERR_DIR_ENTRY_EXISTS, DirEntryExists)
        m_dict.Add(EDS_ERR_DIR_NOT_EMPTY, DirNotEmpty)

        ' Property errors
        m_dict.Add(EDS_ERR_PROPERTIES_UNAVAILABLE, PropertiesUnavailable)
        m_dict.Add(EDS_ERR_PROPERTIES_MISMATCH, PropertiesMismatch)
        m_dict.Add(EDS_ERR_PROPERTIES_NOT_LOADED, PropertiesNotLoaded)

        ' Function Parameter errors
        m_dict.Add(EDS_ERR_INVALID_PARAMETER, InvalidParameter)
        m_dict.Add(EDS_ERR_INVALID_HANDLE, InvalidHandle)
        m_dict.Add(EDS_ERR_INVALID_POINTER, InvalidPointer)
        m_dict.Add(EDS_ERR_INVALID_INDEX, InvalidIndex)
        m_dict.Add(EDS_ERR_INVALID_LENGTH, InvalidLength)
        m_dict.Add(EDS_ERR_INVALID_FN_POINTER, InvalidFnPointer)
        m_dict.Add(EDS_ERR_INVALID_SORT_FN, InvalidSortFn)

        ' Device errors
        m_dict.Add(EDS_ERR_DEVICE_NOT_FOUND, DeviceNotFound)
        m_dict.Add(EDS_ERR_DEVICE_BUSY, DeviceBusy)
        m_dict.Add(EDS_ERR_DEVICE_INVALID, DeviceInvalid)
        m_dict.Add(EDS_ERR_DEVICE_EMERGENCY, DeviceEmergency)
        m_dict.Add(EDS_ERR_DEVICE_MEMORY_FULL, DeviceMemoryFull)
        m_dict.Add(EDS_ERR_DEVICE_INTERNAL_ERROR, DeviceInternalError)
        m_dict.Add(EDS_ERR_DEVICE_INVALID_PARAMETER, DeviceInvalidParameter)
        m_dict.Add(EDS_ERR_DEVICE_NO_DISK, DeviceNoDisk)
        m_dict.Add(EDS_ERR_DEVICE_DISK_ERROR, DeviceDiskError)
        m_dict.Add(EDS_ERR_DEVICE_CF_GATE_CHANGED, DeviceCfGateChanged)
        m_dict.Add(EDS_ERR_DEVICE_DIAL_CHANGED, DeviceDialChanged)
        m_dict.Add(EDS_ERR_DEVICE_NOT_INSTALLED, DeviceNotInstalled)
        m_dict.Add(EDS_ERR_DEVICE_STAY_AWAKE, DeviceStayAwake)
        m_dict.Add(EDS_ERR_DEVICE_NOT_RELEASED, DeviceNotReleased)

        ' Stream errors
        m_dict.Add(EDS_ERR_STREAM_IO_ERROR, StreamIoError)
        m_dict.Add(EDS_ERR_STREAM_NOT_OPEN, StreamNotOpen)
        m_dict.Add(EDS_ERR_STREAM_ALREADY_OPEN, StreamAlreadyOpen)
        m_dict.Add(EDS_ERR_STREAM_OPEN_ERROR, StreamOpenError)
        m_dict.Add(EDS_ERR_STREAM_CLOSE_ERROR, StreamCloseError)
        m_dict.Add(EDS_ERR_STREAM_SEEK_ERROR, StreamSeekError)
        m_dict.Add(EDS_ERR_STREAM_TELL_ERROR, StreamTellError)
        m_dict.Add(EDS_ERR_STREAM_READ_ERROR, StreamReadError)
        m_dict.Add(EDS_ERR_STREAM_WRITE_ERROR, StreamWriteError)
        m_dict.Add(EDS_ERR_STREAM_PERMISSION_ERROR, StreamPermissionError)
        m_dict.Add(EDS_ERR_STREAM_COULDNT_BEGIN_THREAD, StreamCouldntBeginThread)
        m_dict.Add(EDS_ERR_STREAM_BAD_OPTIONS, StreamBadOptions)
        m_dict.Add(EDS_ERR_STREAM_END_OF_STREAM, StreamEndOfStream)

        ' Communications errors
        m_dict.Add(EDS_ERR_COMM_PORT_IS_IN_USE, CommPortIsInUse)
        m_dict.Add(EDS_ERR_COMM_DISCONNECTED, CommDisconnected)
        m_dict.Add(EDS_ERR_COMM_DEVICE_INCOMPATIBLE, CommDeviceIncompatible)
        m_dict.Add(EDS_ERR_COMM_BUFFER_FULL, CommBufferFull)
        m_dict.Add(EDS_ERR_COMM_USB_BUS_ERR, CommUsbBusErr)

        ' Lock/Unlock
        m_dict.Add(EDS_ERR_USB_DEVICE_LOCK_ERROR, UsbDeviceLockError)
        m_dict.Add(EDS_ERR_USB_DEVICE_UNLOCK_ERROR, UsbDeviceUnlockError)

        ' STI/WIA
        m_dict.Add(EDS_ERR_STI_UNKNOWN_ERROR, StiUnknownError)
        m_dict.Add(EDS_ERR_STI_INTERNAL_ERROR, StiInternalError)
        m_dict.Add(EDS_ERR_STI_DEVICE_CREATE_ERROR, StiDeviceCreateError)
        m_dict.Add(EDS_ERR_STI_DEVICE_RELEASE_ERROR, StiDeviceReleaseError)
        m_dict.Add(EDS_ERR_DEVICE_NOT_LAUNCHED, DeviceNotLaunched)

        m_dict.Add(EDS_ERR_ENUM_NA, EnumNa)
        m_dict.Add(EDS_ERR_INVALID_FN_CALL, InvalidFnCall)
        m_dict.Add(EDS_ERR_HANDLE_NOT_FOUND, HandleNotFound)
        m_dict.Add(EDS_ERR_INVALID_ID, InvalidId)
        m_dict.Add(EDS_ERR_WAIT_TIMEOUT_ERROR, WaitTimeoutError)

        ' PTP
        m_dict.Add(EDS_ERR_SESSION_NOT_OPEN, SessionNotOpen)
        m_dict.Add(EDS_ERR_INVALID_TRANSACTIONID, InvalidTransactionid)
        m_dict.Add(EDS_ERR_INCOMPLETE_TRANSFER, IncompleteTransfer)
        m_dict.Add(EDS_ERR_INVALID_STRAGEID, InvalidStrageid)
        m_dict.Add(EDS_ERR_DEVICEPROP_NOT_SUPPORTED, DevicepropNotSupported)
        m_dict.Add(EDS_ERR_INVALID_OBJECTFORMATCODE, InvalidObjectformatcode)
        m_dict.Add(EDS_ERR_SELF_TEST_FAILED, SelfTestFailed)
        m_dict.Add(EDS_ERR_PARTIAL_DELETION, PartialDeletion)
        m_dict.Add(EDS_ERR_SPECIFICATION_BY_FORMAT_UNSUPPORTED, SpecificationByFormatUnsupported)
        m_dict.Add(EDS_ERR_NO_VALID_OBJECTINFO, NoValidObjectinfo)
        m_dict.Add(EDS_ERR_INVALID_CODE_FORMAT, InvalidCodeFormat)
        m_dict.Add(EDS_ERR_UNKNOWN_VENDER_CODE, UnknownVenderCode)
        m_dict.Add(EDS_ERR_CAPTURE_ALREADY_TERMINATED, CaptureAlreadyTerminated)
        m_dict.Add(EDS_ERR_INVALID_PARENTOBJECT, InvalidParentobject)
        m_dict.Add(EDS_ERR_INVALID_DEVICEPROP_FORMAT, InvalidDevicepropFormat)
        m_dict.Add(EDS_ERR_INVALID_DEVICEPROP_VALUE, InvalidDevicepropValue)
        m_dict.Add(EDS_ERR_SESSION_ALREADY_OPEN, SessionAlreadyOpen)
        m_dict.Add(EDS_ERR_TRANSACTION_CANCELLED, TransactionCancelled)
        m_dict.Add(EDS_ERR_SPECIFICATION_OF_DESTINATION_UNSUPPORTED, SpecificationOfDestinationUnsupported)
        m_dict.Add(EDS_ERR_UNKNOWN_COMMAND, UnknownCommand)
        m_dict.Add(EDS_ERR_OPERATION_REFUSED, OperationRefused)
        m_dict.Add(EDS_ERR_LENS_COVER_CLOSE, LensCoverClose)
        m_dict.Add(EDS_ERR_LOW_BATTERY, LowBattery)
        m_dict.Add(EDS_ERR_OBJECT_NOTREADY, ObjectNotready)

        m_dict.Add(EDS_ERR_TAKE_PICTURE_AF_NG, TakePictureAfNg)
        m_dict.Add(EDS_ERR_TAKE_PICTURE_RESERVED, TakePictureReserved)
        m_dict.Add(EDS_ERR_TAKE_PICTURE_MIRROR_UP_NG, TakePictureMirrorUpNg)
        m_dict.Add(EDS_ERR_TAKE_PICTURE_SENSOR_CLEANING_NG, TakePictureSensorCleaningNg)
        m_dict.Add(EDS_ERR_TAKE_PICTURE_SILENCE_NG, TakePictureSilenceNg)
        m_dict.Add(EDS_ERR_TAKE_PICTURE_NO_CARD_NG, TakePictureNoCardNg)
        m_dict.Add(EDS_ERR_TAKE_PICTURE_CARD_NG, TakePictureCardNg)
        m_dict.Add(EDS_ERR_TAKE_PICTURE_CARD_PROTECT_NG, TakePictureCardProtectNg)

        ' 44313 ???
    End Sub

    ' Miscellaneous errors
    Public Const Unimplemented = "Unimplemented"
    Public Const InternalError = "Internal Error"
    Public Const MemAllocFailed = "Mem Alloc Failed"
    Public Const MemFreeFailed = "Mem Free Failed"
    Public Const OperationCancelled = "Operation Cancelled"
    Public Const IncompatibleVersion = "Incompatible Version"
    Public Const NotSupported = "Not Supported"
    Public Const UnexpectedException = "Unexpected Exception"
    Public Const ProtectionViolation = "Protection Violation"
    Public Const MissingSubcomponent = "Missing Subcomponent"
    Public Const SelectionUnavailable = "Selection Unavailable"

    ' File errors
    Public Const FileIoError = "File IO Error"
    Public Const FileTooManyOpen = "File Too Many Open"
    Public Const FileNotFound = "File Not Found"
    Public Const FileOpenError = "File Open Error"
    Public Const FileCloseError = "File Close Error"
    Public Const FileSeekError = "File Seek Error"
    Public Const FileTellError = "File Tell Error"
    Public Const FileReadError = "File Read Error"
    Public Const FileWriteError = "File Write Error"
    Public Const FilePermissionError = "File Permission Error"
    Public Const FileDiskFullError = "File Disk Full Error"
    Public Const FileAlreadyExists = "File Already Exists"
    Public Const FileFormatUnrecognized = "File Format Unrecognized"
    Public Const FileDataCorrupt = "File Data Corrupt"
    Public Const FileNamingNa = "File Naming NA"

    ' Directory errors
    Public Const DirNotFound = "Dir Not Found"
    Public Const DirIoError = "Dir IO Error"
    Public Const DirEntryNotFound = "Dir Entry Not Found"
    Public Const DirEntryExists = "Dir Entry Exists"
    Public Const DirNotEmpty = "Dir Not Empty"

    ' Property errors
    Public Const PropertiesUnavailable = "Properties Unavailable"
    Public Const PropertiesMismatch = "Properties Mismatch"
    Public Const PropertiesNotLoaded = "Properties Not Loaded"

    ' Function Parameter errors
    Public Const InvalidParameter = "Invalid Parameter"
    Public Const InvalidHandle = "Invalid Handle"
    Public Const InvalidPointer = "Invalid Pointer"
    Public Const InvalidIndex = "Invalid Index"
    Public Const InvalidLength = "Invalid Length"
    Public Const InvalidFnPointer = "Invalid Function Pointer"
    Public Const InvalidSortFn = "Invalid Sort Function"

    ' Device errors
    Public Const DeviceNotFound = "Device Not Found"
    Public Const DeviceBusy = "Device Busy"
    Public Const DeviceInvalid = "Device Invalid"
    Public Const DeviceEmergency = "Device Emergency"
    Public Const DeviceMemoryFull = "Device Memory Full"
    Public Const DeviceInternalError = "Device Internal Error"
    Public Const DeviceInvalidParameter = "Device Invalid Parameter"
    Public Const DeviceNoDisk = "Device No Disk"
    Public Const DeviceDiskError = "Device Disk Error"
    Public Const DeviceCfGateChanged = "Device CF Gate Changed"
    Public Const DeviceDialChanged = "Device Dial Changed"
    Public Const DeviceNotInstalled = "Device Not Installed"
    Public Const DeviceStayAwake = "Device Stay Awake"
    Public Const DeviceNotReleased = "Device Not Released"

    ' Stream errors
    Public Const StreamIoError = "Stream IO Error"
    Public Const StreamNotOpen = "Stream Not Open"
    Public Const StreamAlreadyOpen = "Stream Already Open"
    Public Const StreamOpenError = "Stream Open Error"
    Public Const StreamCloseError = "Stream Close Error"
    Public Const StreamSeekError = "Stream Seek Error"
    Public Const StreamTellError = "Stream Tell Error"
    Public Const StreamReadError = "Stream Read Error"
    Public Const StreamWriteError = "Stream Write Error"
    Public Const StreamPermissionError = "Stream Permission Error"
    Public Const StreamCouldntBeginThread = "Stream Couldn't Begin Thread"
    Public Const StreamBadOptions = "Stream Bad Options"
    Public Const StreamEndOfStream = "Stream End of Stream"

    ' Communications errors
    Public Const CommPortIsInUse = "Comm Port Is in Use"
    Public Const CommDisconnected = "Comm Disconnected"
    Public Const CommDeviceIncompatible = "Comm Device Incompatible"
    Public Const CommBufferFull = "Comm Buffer Full"
    Public Const CommUsbBusErr = "Comm USB Bus Err"

    ' Lock/Unlock
    Public Const UsbDeviceLockError = "USB Device Lock Error"
    Public Const UsbDeviceUnlockError = "USB Device Unlock Error"

    ' STI/WIA
    Public Const StiUnknownError = "STI Unknown Error"
    Public Const StiInternalError = "STI Internal Error"
    Public Const StiDeviceCreateError = "STI Device Create Error"
    Public Const StiDeviceReleaseError = "STI Device Release Error"
    Public Const DeviceNotLaunched = "Device Not Launched"

    Public Const EnumNa = "Enum NA"
    Public Const InvalidFnCall = "Invalid Function Call"
    Public Const HandleNotFound = "Handle Not Found"
    Public Const InvalidId = "Invalid ID"
    Public Const WaitTimeoutError = "Wait Timeout Error"

    ' PTP
    Public Const SessionNotOpen = "Session Not Open"
    Public Const InvalidTransactionid = "Invalid Transactionid"
    Public Const IncompleteTransfer = "Incomplete Transfer"
    Public Const InvalidStrageid = "Invalid Storage ID"
    Public Const DevicepropNotSupported = "Deviceprop Not Supported"
    Public Const InvalidObjectformatcode = "Invalid Object Format Code"
    Public Const SelfTestFailed = "Self Test Failed"
    Public Const PartialDeletion = "Partial Deletion"
    Public Const SpecificationByFormatUnsupported = "Specification by Format Unsupported"
    Public Const NoValidObjectinfo = "No Valid Object Info"
    Public Const InvalidCodeFormat = "Invalid Code Format"
    Public Const UnknownVenderCode = "Unknown Vender Code"
    Public Const CaptureAlreadyTerminated = "Capture Already Terminated"
    Public Const InvalidParentobject = "Invalid Parent Object"
    Public Const InvalidDevicepropFormat = "Invalid Deviceprop Format"
    Public Const InvalidDevicepropValue = "Invalid Deviceprop Value"
    Public Const SessionAlreadyOpen = "Session Already Open"
    Public Const TransactionCancelled = "Transaction Cancelled"
    Public Const SpecificationOfDestinationUnsupported = "Specification of Destination Unsupported"
    Public Const UnknownCommand = "Unknown Command"
    Public Const OperationRefused = "Operation Refused"
    Public Const LensCoverClose = "Lens Cover Close"
    Public Const LowBattery = "Low Battery"
    Public Const ObjectNotready = "Object Not Ready"

    Public Const TakePictureAfNg = "Take Picture AF NG"
    Public Const TakePictureReserved = "Take Picture Reserved"
    Public Const TakePictureMirrorUpNg = "Take Picture Mirror up NG"
    Public Const TakePictureSensorCleaningNg = "Take Picture Sensor Cleaning NG"
    Public Const TakePictureSilenceNg = "Take Picture Silence NG"
    Public Const TakePictureNoCardNg = "Take Picture No Card NG"
    Public Const TakePictureCardNg = "Take Picture Card NG"
    Public Const TakePictureCardProtectNg = "Take Picture Card Protect NG"

#End Region

End Class

Public Class GetOnlyCameraException
    Inherits Exception
End Class
Public Class NoCameraFoundException
    Inherits GetOnlyCameraException
End Class
Public Class TooManyCamerasFoundException
    Inherits GetOnlyCameraException
End Class
Public Class OnlyOneInstanceAllowedException
    Inherits Exception
End Class
Public Class TakePictureFailedException
    Inherits Exception
End Class
Public Class CameraIsBusyException
    Inherits Exception
End Class
Public Class LiveViewFailedException
    Inherits Exception
End Class
Public Class CameraDisconnectedException
    Inherits Exception
End Class
Public Class DirectoryDoesNotExistException
    Inherits Exception
End Class