﻿using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace NAudio.Wave
{
    /// <summary>
    /// NativeDirectSoundOut using DirectSound COM interop.
    /// WARNING, This class is working only if application is running in MTAThread mode.
    /// TODO: Change code to support STAThread. Need some rework and transfer all COM code to a single thread.
    /// TODO: Add better exception/error handling 
    /// Contact author: Alexandre Mutel - alexandre_mutel at yahoo.fr
    /// </summary>
    public class NativeDirectSoundOut : IWavePlayer
    {
        private PlaybackState playbackState;
        private WaveFormat waveFormat;
        private int samplesTotalSize;
        private int samplesFrameSize;
        private int nextSamplesWriteIndex;
        private int desiredLatency;
        private byte[] samples;
        private WaveStream waveStream = null;
        private IDirectSound directSound = null;
        private IDirectSoundBuffer primarySoundBuffer = null;
        private IDirectSoundBuffer secondaryBuffer = null;
        private EventWaitHandle frameEventWaitHandle1;
        private EventWaitHandle frameEventWaitHandle2;
        private EventWaitHandle endEventWaitHandle;
        private Thread notifyThread;

        /// <summary>
        /// Initializes a new instance of the <see cref="NativeDirectSoundOut"/> class.
        /// </summary>
        public NativeDirectSoundOut()
            : this(40)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NativeDirectSoundOut"/> class.
        /// (40ms seems to work under Vista).
        /// </summary>
        /// <param name="latency">The latency.</param>
        public NativeDirectSoundOut(int latency)
        {
            desiredLatency = latency;
        }

        /// <summary>
        /// Releases unmanaged resources and performs other cleanup operations before the
        /// <see cref="NativeDirectSoundOut"/> is reclaimed by garbage collection.
        /// </summary>
        ~NativeDirectSoundOut()
        {
            Dispose();
        }

        /// <summary>
        /// Begin playback
        /// </summary>
        public void Play()
        {
            if (playbackState == PlaybackState.Stopped)
            {
                // -------------------------------------------------------------------------------------
                // Thread that process samples
                // -------------------------------------------------------------------------------------
                notifyThread = new Thread(processSamples);
                // put this back to highest when we are confident we don't have any bugs in the thread proc
                notifyThread.Priority = ThreadPriority.Normal;
                notifyThread.IsBackground = true;
                notifyThread.Start();
            }
            playbackState = PlaybackState.Playing;
        }

        /// <summary>
        /// Stop playback
        /// </summary>
        public void Stop()
        {
            playbackState = PlaybackState.Stopped;
            if (notifyThread != null)
            {
                notifyThread.Join();
                notifyThread = null;
            }
        }

        /// <summary>
        /// Pause Playback
        /// </summary>
        public void Pause()
        {
            playbackState = PlaybackState.Paused;
        }

        /// <summary>
        /// Initialise playback
        /// </summary>
        /// <param name="waveStreamArg">The wavestream to be played</param>
        public void Init(WaveStream waveStreamArg)
        {
            waveStream = waveStreamArg;
            waveFormat = waveStream.WaveFormat;
        }

        private void InitialiseDirectSound()
        {
            // Open DirectSound
            DirectSoundCreate(IntPtr.Zero, out directSound, IntPtr.Zero);

            // Set Cooperative Level to PRIORITY (priority level can call the SetFormat and Compact methods)
            directSound.SetCooperativeLevel(GetDesktopWindow(), DirectSoundCooperativeLevel.DSSCL_PRIORITY);

            // -------------------------------------------------------------------------------------
            // Create PrimaryBuffer
            // -------------------------------------------------------------------------------------

            // Fill BufferDescription for PrimaryBuffer
            BufferDescription bufferDesc = new BufferDescription();
            bufferDesc.dwSize = Marshal.SizeOf(bufferDesc);
            bufferDesc.dwBufferBytes = 0;
            bufferDesc.dwFlags = DirectSoundBufferCaps.DSBCAPS_PRIMARYBUFFER;
            bufferDesc.dwReserved = 0;
            bufferDesc.lpwfxFormat = IntPtr.Zero;
            bufferDesc.guidAlgo = Guid.Empty;

            object soundBufferObj;
            // Create PrimaryBuffer
            directSound.CreateSoundBuffer(bufferDesc, out soundBufferObj, IntPtr.Zero);
            primarySoundBuffer = (IDirectSoundBuffer)soundBufferObj;

            // Play & Loop on the PrimarySound Buffer 
            primarySoundBuffer.Play(0, 0, DirectSoundPlayFlags.DSBPLAY_LOOPING);

            // -------------------------------------------------------------------------------------
            // Create SecondaryBuffer
            // -------------------------------------------------------------------------------------

            // A frame of samples equals to Desired Latency
            samplesFrameSize = MsToBytes(desiredLatency);

            // Fill BufferDescription for SecondaryBuffer
            BufferDescription bufferDesc2 = new BufferDescription();
            bufferDesc2.dwSize = Marshal.SizeOf(bufferDesc2);
            bufferDesc2.dwBufferBytes = (uint)(samplesFrameSize * 2);
            bufferDesc2.dwFlags = DirectSoundBufferCaps.DSBCAPS_GETCURRENTPOSITION2
                | DirectSoundBufferCaps.DSBCAPS_CTRLPOSITIONNOTIFY
                | DirectSoundBufferCaps.DSBCAPS_GLOBALFOCUS
                | DirectSoundBufferCaps.DSBCAPS_CTRLVOLUME
                | DirectSoundBufferCaps.DSBCAPS_STICKYFOCUS;
            bufferDesc2.dwReserved = 0;
            GCHandle handleOnWaveFormat = GCHandle.Alloc(waveFormat, GCHandleType.Pinned); // Ptr to waveFormat
            bufferDesc2.lpwfxFormat = handleOnWaveFormat.AddrOfPinnedObject(); // set Ptr to waveFormat
            bufferDesc2.guidAlgo = Guid.Empty;

            // Create SecondaryBuffer
            directSound.CreateSoundBuffer(bufferDesc2, out soundBufferObj, IntPtr.Zero);
            secondaryBuffer = (IDirectSoundBuffer)soundBufferObj;
            handleOnWaveFormat.Free();

            // Get effective SecondaryBuffer size
            BufferCaps dsbCaps = new BufferCaps();
            dsbCaps.dwSize = Marshal.SizeOf(dsbCaps);
            secondaryBuffer.GetCaps(dsbCaps);

            nextSamplesWriteIndex = 0;
            samplesTotalSize = dsbCaps.dwBufferBytes;
            samples = new byte[samplesTotalSize];
            System.Diagnostics.Debug.Assert(samplesTotalSize == (2 * samplesFrameSize), "Invalid SamplesTotalSize vs SamplesFrameSize");

            // -------------------------------------------------------------------------------------
            // Create double buffering notification.
            // Use DirectSoundNotify at Position [0, 1/2] and Stop Position (0xFFFFFFFF)
            // -------------------------------------------------------------------------------------
            IDirectSoundNotify notify = (IDirectSoundNotify)soundBufferObj;

            frameEventWaitHandle1 = new EventWaitHandle(false, EventResetMode.AutoReset);
            frameEventWaitHandle2 = new EventWaitHandle(false, EventResetMode.AutoReset);
            endEventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);

            DirectSoundBufferPositionNotify[] notifies = new DirectSoundBufferPositionNotify[3];
            notifies[0] = new DirectSoundBufferPositionNotify();
            notifies[0].dwOffset = 0;
            notifies[0].hEventNotify = frameEventWaitHandle1.SafeWaitHandle.DangerousGetHandle();

            notifies[1] = new DirectSoundBufferPositionNotify();
            notifies[1].dwOffset = (uint)samplesFrameSize;
            notifies[1].hEventNotify = frameEventWaitHandle2.SafeWaitHandle.DangerousGetHandle();

            notifies[2] = new DirectSoundBufferPositionNotify();
            notifies[2].dwOffset = 0xFFFFFFFF;
            notifies[2].hEventNotify = endEventWaitHandle.SafeWaitHandle.DangerousGetHandle();

            notify.SetNotificationPositions(3, notifies);
        }

        /// <summary>
        /// Current playback state
        /// </summary>
        /// <value></value>
        public PlaybackState PlaybackState
        {
            get { return playbackState; }
        }

        /// <summary>
        /// The volume 1.0 is full scale
        /// </summary>
        /// <value></value>
        public float Volume
        {
            get
            {
                return 1.0f;
                //return 1 + (secondaryBuffer.GetVolume()) / 10000.0f;
            }
            set
            {
                //int intVol = (int)((value - 1) * 10000.0f);
                //secondaryBuffer.SetVolume(intVol);
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Determines whether the SecondaryBuffer is lost.
        /// </summary>
        /// <returns>
        /// 	<c>true</c> if [is buffer lost]; otherwise, <c>false</c>.
        /// </returns>
        private bool IsBufferLost()
        {
            return (secondaryBuffer.GetStatus() & DirectSoundBufferStatus.DSBSTATUS_BUFFERLOST) != 0 ? true : false;
        }

        /// <summary>
        /// Convert ms to bytes size according to WaveFormat
        /// </summary>
        /// <param name="ms">The ms</param>
        /// <returns>number of byttes</returns>
        private int MsToBytes(int ms)
        {
            int bytes = ms * (waveFormat.AverageBytesPerSecond / 1000);
            bytes -= bytes % waveFormat.BlockAlign;
            return bytes;
        }

        /// <summary>
        /// Processes the samples in a separate thread.
        /// </summary>
        private void processSamples()
        {
            InitialiseDirectSound();

            if (PlaybackState == PlaybackState.Stopped)
            {
                secondaryBuffer.SetCurrentPosition(0);
                nextSamplesWriteIndex = 0;
                Feed(samplesTotalSize);
            }
            playbackState = PlaybackState.Playing;
            secondaryBuffer.Play(0, 0, DirectSoundPlayFlags.DSBPLAY_LOOPING);

            WaitHandle[] waitHandles = new WaitHandle[] { frameEventWaitHandle1, frameEventWaitHandle2, endEventWaitHandle };

            while (PlaybackState != PlaybackState.Stopped)
            {
                // Wait for signals on frameEventWaitHandle1 (Position 0), frameEventWaitHandle2 (Position 1/2)
                int indexHandle = WaitHandle.WaitAny(waitHandles, 3 * desiredLatency, false);

                // TimeOut is ok
                if (indexHandle != WaitHandle.WaitTimeout)
                {
                    // Buffer is Stopped
                    if (indexHandle == 2)
                    {

                    }
                    else
                    {
                        indexHandle = (indexHandle == 0) ? 1 : 0;
                        nextSamplesWriteIndex = indexHandle * samplesFrameSize;
                        Feed(samplesFrameSize);
                    }
                }
            }

            if (secondaryBuffer != null)
            {
                secondaryBuffer.Stop();
                secondaryBuffer = null;
            }
            if (primarySoundBuffer != null)
            {
                primarySoundBuffer.Stop();
                primarySoundBuffer = null;
            }

        }


        /// <summary>
        /// Feeds the SecondaryBuffer with the WaveStream
        /// </summary>
        /// <param name="bytesToCopy">number of bytes to feed</param>
        private void Feed(int bytesToCopy)
        {
            int bytesRead = bytesToCopy;
            // Restore the buffer if lost
            if (IsBufferLost())
            {
                secondaryBuffer.Restore();
            }

            // Clear the bufferSamples if in Paused
            if (playbackState == PlaybackState.Paused)
            {
                Array.Clear(samples, 0, samples.Length);
            }
            else
            {
                // Read data from stream (Should this be inserted between the lock / unlock?)
                bytesRead = waveStream.Read(samples, 0, bytesToCopy);
            }

            // Lock a portion of the SecondaryBuffer (starting from 0 or 1/2 the buffer)
            IntPtr wavBuffer1;
            int nbSamples1;
            IntPtr wavBuffer2;
            int nbSamples2;
            secondaryBuffer.Lock(nextSamplesWriteIndex, (uint)bytesRead,  // (uint)bytesToCopy,
                                 out wavBuffer1, out nbSamples1,
                                 out wavBuffer2, out nbSamples2,
                                 DirectSoundBufferLockFlag.None);

            // Copy back to the SecondaryBuffer
            if (wavBuffer1 != IntPtr.Zero)
            {
                Marshal.Copy(samples, 0, wavBuffer1, nbSamples1);
                if (wavBuffer2 != IntPtr.Zero)
                {
                    Marshal.Copy(samples, 0, wavBuffer1, nbSamples1);
                }
            }

            // Unlock the SecondaryBuffer
            secondaryBuffer.Unlock(wavBuffer1, nbSamples1, wavBuffer2, nbSamples2);
        }


        //----------------------------------------------------------------------------------------------
        // Minimal Native DirectSound COM interop interfaces
        //----------------------------------------------------------------------------------------------
        #region Native DirectSound COM Interface

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal class BufferDescription
        {
            public int dwSize;
            [MarshalAs(UnmanagedType.U4)]
            public DirectSoundBufferCaps dwFlags;
            public uint dwBufferBytes;
            public int dwReserved;
            public IntPtr lpwfxFormat;
            public Guid guidAlgo;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal class BufferCaps
        {
            public int dwSize;
            public int dwFlags;
            public int dwBufferBytes;
            public int dwUnlockTransferRate;
            public int dwPlayCpuOverhead;
        }

        internal enum DirectSoundCooperativeLevel : uint
        {
            DSSCL_NORMAL = 0x00000001,
            DSSCL_PRIORITY = 0x00000002,
            DSSCL_EXCLUSIVE = 0x00000003,
            DSSCL_WRITEPRIMARY = 0x00000004
        }

        [FlagsAttribute]
        internal enum DirectSoundPlayFlags : uint
        {
            DSBPLAY_LOOPING = 0x00000001,
            DSBPLAY_LOCHARDWARE = 0x00000002,
            DSBPLAY_LOCSOFTWARE = 0x00000004,
            DSBPLAY_TERMINATEBY_TIME = 0x00000008,
            DSBPLAY_TERMINATEBY_DISTANCE = 0x000000010,
            DSBPLAY_TERMINATEBY_PRIORITY = 0x000000020
        }

        internal enum DirectSoundBufferLockFlag : uint
        {
            None = 0,
            FromWriteCursor = 0x00000001,
            EntireBuffer = 0x00000002
        }

        [FlagsAttribute]
        internal enum DirectSoundBufferStatus : uint
        {
            DSBSTATUS_PLAYING = 0x00000001,
            DSBSTATUS_BUFFERLOST = 0x00000002,
            DSBSTATUS_LOOPING = 0x00000004,
            DSBSTATUS_LOCHARDWARE = 0x00000008,
            DSBSTATUS_LOCSOFTWARE = 0x00000010,
            DSBSTATUS_TERMINATED = 0x00000020
        }

        [FlagsAttribute]
        internal enum DirectSoundBufferCaps : uint
        {
            DSBCAPS_PRIMARYBUFFER = 0x00000001,
            DSBCAPS_STATIC = 0x00000002,
            DSBCAPS_LOCHARDWARE = 0x00000004,
            DSBCAPS_LOCSOFTWARE = 0x00000008,
            DSBCAPS_CTRL3D = 0x00000010,
            DSBCAPS_CTRLFREQUENCY = 0x00000020,
            DSBCAPS_CTRLPAN = 0x00000040,
            DSBCAPS_CTRLVOLUME = 0x00000080,
            DSBCAPS_CTRLPOSITIONNOTIFY = 0x00000100,
            DSBCAPS_CTRLFX = 0x00000200,
            DSBCAPS_STICKYFOCUS = 0x00004000,
            DSBCAPS_GLOBALFOCUS = 0x00008000,
            DSBCAPS_GETCURRENTPOSITION2 = 0x00010000,
            DSBCAPS_MUTE3DATMAXDISTANCE = 0x00020000,
            DSBCAPS_LOCDEFER = 0x00040000
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal struct DirectSoundBufferPositionNotify
        {
            [MarshalAs(UnmanagedType.I4)]
            public uint dwOffset;
            public IntPtr hEventNotify;
        }

        /// <summary>
        /// IDirectSound interface
        /// </summary>
        [ComImport,
         Guid("279AFA83-4981-11CE-A521-0020AF0BE560"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         SuppressUnmanagedCodeSecurity]
        internal interface IDirectSound
        {
            //STDMETHOD(CreateSoundBuffer)    (THIS_ LPCDSBUFFERDESC pcDSBufferDesc, LPDIRECTSOUNDBUFFER *ppDSBuffer, LPUNKNOWN pUnkOuter) PURE;
            void CreateSoundBuffer([In] BufferDescription desc, [Out, MarshalAs(UnmanagedType.Interface)] out object dsDSoundBuffer, IntPtr pUnkOuter);
            //STDMETHOD(GetCaps)              (THIS_ LPDSCAPS pDSCaps) PURE;
            void GetCaps(IntPtr caps);
            //STDMETHOD(DuplicateSoundBuffer) (THIS_ LPDIRECTSOUNDBUFFER pDSBufferOriginal, LPDIRECTSOUNDBUFFER *ppDSBufferDuplicate) PURE;
            void DuplicateSoundBuffer([In, MarshalAs(UnmanagedType.Interface)] IDirectSoundBuffer bufferOriginal, [In, MarshalAs(UnmanagedType.Interface)] IDirectSoundBuffer bufferDuplicate);
            //STDMETHOD(SetCooperativeLevel)  (THIS_ HWND hwnd, DWORD dwLevel) PURE;
            void SetCooperativeLevel(IntPtr HWND, [In, MarshalAs(UnmanagedType.U4)] DirectSoundCooperativeLevel dwLevel);
            //STDMETHOD(Compact)              (THIS) PURE;
            void Compact();
            //STDMETHOD(GetSpeakerConfig)     (THIS_ LPDWORD pdwSpeakerConfig) PURE;
            void GetSpeakerConfig(IntPtr pdwSpeakerConfig);
            //STDMETHOD(SetSpeakerConfig)     (THIS_ DWORD dwSpeakerConfig) PURE;
            void SetSpeakerConfig(uint pdwSpeakerConfig);
            //STDMETHOD(Initialize)           (THIS_ LPCGUID pcGuidDevice) PURE;
            void Initialize([In, MarshalAs(UnmanagedType.LPStruct)] Guid guid);
        }

        /// <summary>
        /// IDirectSoundBuffer interface
        /// </summary>
        [ComImport,
         Guid("279AFA85-4981-11CE-A521-0020AF0BE560"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         SuppressUnmanagedCodeSecurity]
        internal interface IDirectSoundBuffer
        {
            //    STDMETHOD(GetCaps)              (THIS_ LPDSBCAPS pDSBufferCaps) PURE;
            void GetCaps([MarshalAs(UnmanagedType.LPStruct)] BufferCaps pBufferCaps);
            //    STDMETHOD(GetCurrentPosition)   (THIS_ LPDWORD pdwCurrentPlayCursor, LPDWORD pdwCurrentWriteCursor) PURE;
            void GetCurrentPosition([Out] out uint currentPlayCursor, [Out] out uint currentWriteCursor);
            //    STDMETHOD(GetFormat)            (THIS_ LPWAVEFORMATEX pwfxFormat, DWORD dwSizeAllocated, LPDWORD pdwSizeWritten) PURE;
            void GetFormat();
            //    STDMETHOD(GetVolume)            (THIS_ LPLONG plVolume) PURE;
            [return: MarshalAs(UnmanagedType.I4)]
            int GetVolume();
            //    STDMETHOD(GetPan)               (THIS_ LPLONG plPan) PURE;
            void GetPan([Out] out uint pan);
            //    STDMETHOD(GetFrequency)         (THIS_ LPDWORD pdwFrequency) PURE;
            [return: MarshalAs(UnmanagedType.I4)]
            int GetFrequency();
            //    STDMETHOD(GetStatus)            (THIS_ LPDWORD pdwStatus) PURE;
            [return: MarshalAs(UnmanagedType.U4)]
            DirectSoundBufferStatus GetStatus();
            //    STDMETHOD(Initialize)           (THIS_ LPDIRECTSOUND pDirectSound, LPCDSBUFFERDESC pcDSBufferDesc) PURE;
            void Initialize([In, MarshalAs(UnmanagedType.Interface)] IDirectSound directSound, [In] BufferDescription desc);
            //    STDMETHOD(Lock)                 (THIS_ DWORD dwOffset, DWORD dwBytes, LPVOID *ppvAudioPtr1, LPDWORD pdwAudioBytes1,
            //                                           LPVOID *ppvAudioPtr2, LPDWORD pdwAudioBytes2, DWORD dwFlags) PURE;
            void Lock(int dwOffset, uint dwBytes, [Out] out IntPtr audioPtr1, [Out] out int audioBytes1, [Out] out IntPtr audioPtr2, [Out] out int audioBytes2, [MarshalAs(UnmanagedType.U4)] DirectSoundBufferLockFlag dwFlags);
            //    STDMETHOD(Play)                 (THIS_ DWORD dwReserved1, DWORD dwPriority, DWORD dwFlags) PURE;
            void Play(uint dwReserved1, uint dwPriority, [In, MarshalAs(UnmanagedType.U4)] DirectSoundPlayFlags dwFlags);
            //    STDMETHOD(SetCurrentPosition)   (THIS_ DWORD dwNewPosition) PURE;
            void SetCurrentPosition(uint dwNewPosition);
            //    STDMETHOD(SetFormat)            (THIS_ LPCWAVEFORMATEX pcfxFormat) PURE;
            void SetFormat([In] WaveFormat pcfxFormat);
            //    STDMETHOD(SetVolume)            (THIS_ LONG lVolume) PURE;
            void SetVolume(int volume);
            //    STDMETHOD(SetPan)               (THIS_ LONG lPan) PURE;
            void SetPan(uint pan);
            //    STDMETHOD(SetFrequency)         (THIS_ DWORD dwFrequency) PURE;
            void SetFrequency(uint frequency);
            //    STDMETHOD(Stop)                 (THIS) PURE;
            void Stop();
            //    STDMETHOD(Unlock)               (THIS_ LPVOID pvAudioPtr1, DWORD dwAudioBytes1, LPVOID pvAudioPtr2, DWORD dwAudioBytes2) PURE;
            void Unlock(IntPtr pvAudioPtr1, int dwAudioBytes1, IntPtr pvAudioPtr2, int dwAudioBytes2);
            //    STDMETHOD(Restore)              (THIS) PURE;
            void Restore();
        }

        /// <summary>
        /// IDirectSoundNotify interface
        /// </summary>
        [ComImport,
         Guid("b0210783-89cd-11d0-af08-00a0c925cd16"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         SuppressUnmanagedCodeSecurity]
        internal interface IDirectSoundNotify
        {
            void SetNotificationPositions(int dwPositionNotifies, [In, MarshalAs(UnmanagedType.LPArray)] DirectSoundBufferPositionNotify[] pcPositionNotifies);
            //            void SetNotificationPositions(int dwPositionNotifies, IntPtr pcPositionNotifies);
        }

        /// <summary>
        /// Instanciate DirectSound from the DLL
        /// </summary>
        /// <param name="GUID">The GUID.</param>
        /// <param name="directSound">The direct sound.</param>
        /// <param name="pUnkOuter">The p unk outer.</param>
        [DllImport("dsound.dll", EntryPoint = "DirectSoundCreate", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        static extern void DirectSoundCreate(IntPtr GUID, [Out, MarshalAs(UnmanagedType.Interface)] out IDirectSound directSound, IntPtr pUnkOuter);

        /// <summary>
        /// Gets the HANDLE of the desktop window.
        /// </summary>
        /// <returns>HANDLE of the Desktop window</returns>
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();
        #endregion
    }
}