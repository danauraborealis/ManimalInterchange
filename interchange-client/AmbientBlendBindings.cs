using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Audio.AmbientSubsystem;
using Audio.AmbientSubsystem.Data;
using Audio.AuxiliaryAudioUtils;
using Audio.Data;
using Audio.Extensions;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using Audio.SpatialSystem.Utils;
using CommonAssets.Scripts.Audio;
using Comfort.Common;
using EFT;
using EFT.DataProviding;
using EFT.GlobalEvents;
using EFT.GlobalEvents.AudioEvents;
using HarmonyLib;
using Manimal.Interchange.Components;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.Audio;

namespace Manimal.Interchange.Client;

/// <summary>
/// Replays the source ambient blender/system contract on the target audio
/// services.  The target classes remain the scene owners, while this binding
/// owns the two authored mixer parameter sets, directional fades, source
/// filter curves and source portal score.  This avoids invoking the target
/// system's reduced scheduler, which would silently discard the source depth
/// and connected/neighbor penalties.
/// </summary>
internal static class AmbientBlendBindings
{
    internal sealed class BlenderBinding
    {
        internal readonly AmbientBlendPolicy Policy;
        internal readonly AmbientSoundBlender Target;
        internal AudioMixer? Master;
        internal IAudioMixerFader? OutdoorFader;
        internal IAudioMixerFader? IndoorFader;
        internal float CurrentOutdoorFilterValue;
        internal bool Initialized;
        internal bool Disposed;

        internal BlenderBinding(AmbientBlendPolicy policy, AmbientSoundBlender target)
        {
            Policy = policy;
            Target = target;
        }

        internal void Initialize(AudioMixer master)
        {
            if (Disposed) return;
            if (Initialized && ReferenceEquals(Master, master)) return;
            DisposeFaders();
            if (!Policy.OutdoorMixerParams)
                throw new InvalidOperationException("Ambient outdoor mixer parameters are missing: " + Policy.SourceKey);
            if (!Policy.IndoorMixerParams)
                throw new InvalidOperationException("Ambient indoor mixer parameters are missing: " + Policy.SourceKey);
            if (Policy.VolumeCurve == null || Policy.OcclusionCurve == null)
                throw new InvalidOperationException("Ambient blender curves are missing: " + Policy.SourceKey);
            Master = master;
            OutdoorFader = new AudioMixerFader(Target, master);
            IndoorFader = new AudioMixerFader(Target, master);
            CurrentOutdoorFilterValue = 1f;
            Initialized = true;
        }

        internal void SetOutdoor(float value, float maxOutdoorVolume, float maxOutdoorFreq,
            float targetBlendDuration, bool force, int? fadeType)
        {
            EnsureReady();
            var occlusion = Policy.OcclusionCurve.Evaluate(value);
            // The native path keeps the raw occlusion curve for the Nyquist
            // cutoff conversion.  It clamps a separate copy only for the
            // high-pass interpolation; clamping here changes authored curve
            // values above/below [0,1] and loses source behavior.
            var clampedOcclusion = Mathf.Clamp01(occlusion);
            var normalizedVolume = Policy.VolumeCurve.Evaluate(value);
            // The source native method floors the curve to the room's
            // maximum outdoor volume before converting to dB.
            normalizedVolume = Mathf.Max(normalizedVolume, maxOutdoorVolume);
            normalizedVolume = Mathf.Clamp(normalizedVolume, 0.0001f, 1f);
            var volumeDb = EFT.AudioUtils.ConvertNormalizedVolumeToDB(normalizedVolume);
            var cutoff = EFT.AudioUtils.ConvertNormalizedValueToCutoffFrequency(occlusion);
            // Source interpolates from the room's maximum outdoor frequency
            // to the blender's authored maximum using the outdoor filter
            // value. The resulting floor and authored maximum bound the
            // raw converted cutoff.
            var outdoorFilter = Mathf.Clamp01(value);
            var lowpass = Mathf.Lerp(maxOutdoorFreq, Policy.MaxLowpassFreq, outdoorFilter);
            lowpass = Mathf.Max(Policy.MinLowpassFreq, lowpass);
            cutoff = Mathf.Clamp(cutoff, lowpass, Policy.MaxLowpassFreq);
            var highpass = Mathf.Lerp(Policy.MinHighPassFreq, Policy.MaxHighPassFreq, clampedOcclusion);
            var reverb = Mathf.Clamp(volumeDb, -80f, Policy.MaxOutdoorReverbSendDb);
            var delay = Mathf.Clamp(volumeDb, -80f, Policy.MaxOutdoorDelaySendDb);
            var selectedFade = (EAudioFadeType)(fadeType ?? Policy.FadeOutType);

            if (!force && targetBlendDuration > 0f)
            {
                ApplyOutdoorMixerParams(volumeDb, cutoff, highpass, reverb, delay, targetBlendDuration, selectedFade);
            }
            else
            {
                if (OutdoorFader!.IsFading) OutdoorFader.StopFade();
                ApplyOutdoorMixerParams(volumeDb, cutoff, highpass, reverb, delay, 0f, selectedFade);
            }
            CurrentOutdoorFilterValue = value;
        }

        internal void SetIndoor(float value, float targetBlendDuration, bool force, int? fadeType)
        {
            EnsureReady();
            var normalizedVolume = Mathf.Clamp(value, 0.0001f, 1f);
            var volumeDb = EFT.AudioUtils.ConvertNormalizedVolumeToDB(normalizedVolume);
            var selectedFade = (EAudioFadeType)(fadeType ?? Policy.FadeInType);
            if (!force && targetBlendDuration > 0f)
            {
                ApplyMixerParameter(EMixerParameterType.Volume, IndoorParameters(), IndoorFader!,
                    volumeDb, targetBlendDuration, selectedFade);
                return;
            }
            if (IndoorFader!.IsFading) IndoorFader.StopFade();
            ApplyMixerParameter(EMixerParameterType.Volume, IndoorParameters(), IndoorFader!,
                volumeDb, 0f, selectedFade);
        }

        internal void BlendToIndoor()
            => SetIndoor(1f, Policy.FromOutToInTransitionTime, false, Policy.FadeInType);

        internal void BlendToOutdoor()
        {
            SetOutdoor(1f, 1f, 22000f, Policy.FadeOutDuration, false, Policy.FadeOutType);
            SetIndoor(0f, Policy.FromInToOutTransitionTime, false, Policy.FadeOutType);
        }

        internal void BlendToTargetValue(float value, float maxOutdoorVolume, float maxOutdoorFreq)
            => SetOutdoor(value, maxOutdoorVolume, maxOutdoorFreq, Policy.FadeInDuration, false, Policy.FadeInType);

        internal void BlendToTargetPerFrame(float targetValue, float maxOutdoorFreq, float dt)
        {
            var t = dt * Policy.NoPortalBlendSpeed;
            t = Mathf.Clamp01(t);
            var next = Mathf.Lerp(CurrentOutdoorFilterValue, targetValue, t);
            // Native no-portal updates pass targetValue as maxOutdoorVolume,
            // use the fixed 0.5-second default and force an immediate mixer
            // write.  This deliberately leaves no fading coroutine behind.
            SetOutdoor(next, targetValue, maxOutdoorFreq, 0.5f, true, null);
        }

        internal void CalculateAndApply(PortalCalculatedData portalData, Vector3 listenerPos,
            float maxOutdoorVolume, float maxOutdoorFreq, float depthCalculationMult,
            float listenerHeightWeight, float dt)
        {
            var t = Mathf.Clamp01(dt * Policy.BlendSpeed);
            var portal = portalData.portalData;
            var distance = portalData.distanceToListener;
            if (listenerHeightWeight > 0f)
            {
                var vertical = 1f - SoundOcclusionUtils.GetVerticalMult(portal.position, listenerPos);
                distance += distance * Mathf.Abs(vertical) * listenerHeightWeight;
            }
            var target = CalculateOutdoorFilterValue(portal.depth, distance, depthCalculationMult);
            var next = Mathf.Lerp(CurrentOutdoorFilterValue, target, t);
            // The source calculation path passes the default duration and
            // immediate=true, so this is a direct per-frame mixer update.
            SetOutdoor(next, maxOutdoorVolume, maxOutdoorFreq, 0.5f, true, null);
        }

        internal float CalculateOutdoorFilterValue(float portalDepth, float currentDistance, float depthMult)
        {
            var value = Mathf.InverseLerp(portalDepth * depthMult, 0f, currentDistance);
            return Mathf.Clamp01(value);
        }

        private AudioMixerParametersData OutdoorParameters()
            => Policy.OutdoorMixerParams as AudioMixerParametersData
                ?? throw new InvalidOperationException("Ambient outdoor mixer reference has the wrong target type: " + Policy.SourceKey);

        private AudioMixerParametersData IndoorParameters()
            => Policy.IndoorMixerParams as AudioMixerParametersData
                ?? throw new InvalidOperationException("Ambient indoor mixer reference has the wrong target type: " + Policy.SourceKey);

        private void ApplyOutdoorMixerParams(float volumeDb, float lowPassFreq, float highPassFreq,
            float reverbSendDb, float delaySendDb, float duration, EAudioFadeType fadeType)
        {
            var parameters = OutdoorParameters();
            ApplyMixerParameter(EMixerParameterType.LowPassFreq, parameters, OutdoorFader!, lowPassFreq, duration, fadeType);
            ApplyMixerParameter(EMixerParameterType.HighPassFreq, parameters, OutdoorFader!, highPassFreq, duration, fadeType);
            ApplyMixerParameter(EMixerParameterType.Volume, parameters, OutdoorFader!, volumeDb, duration, fadeType);
            ApplyMixerParameter(EMixerParameterType.ReverbSend, parameters, OutdoorFader!, reverbSendDb, duration, fadeType);
            ApplyMixerParameter(EMixerParameterType.DelaySend, parameters, OutdoorFader!, delaySendDb, duration, fadeType);
        }

        private void ApplyMixerParameter(EMixerParameterType parameterType, AudioMixerParametersData data,
            IAudioMixerFader fader, float value, float duration, EAudioFadeType fadeType)
        {
            if (!data.TryGetParameters(parameterType, out var parameters)) return;
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                if (duration > 0f) fader.StartFade(parameter, value, fadeType, duration);
                else Master!.SetFloat(parameter, value);
            }
        }

        private void EnsureReady()
        {
            if (Disposed || !Initialized || Master == null || OutdoorFader == null || IndoorFader == null)
                throw new InvalidOperationException("Ambient blender is not initialized: " + Policy.SourceKey);
        }

        internal void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            DisposeFaders();
        }

        private void DisposeFaders()
        {
            OutdoorFader?.Dispose();
            IndoorFader?.Dispose();
            OutdoorFader = null;
            IndoorFader = null;
            Master = null;
            Initialized = false;
        }
    }

    internal sealed class SystemBinding
    {
        internal readonly AmbientBlendPolicy Policy;
        internal readonly EnvironmentSoundBlendSystem Target;
        internal readonly List<BlenderBinding> Blenders = new();

        private readonly List<Action> _unsubscribers = new();
        private readonly List<short> _neighborBuffer = new();
        private PortalData[] _currentOutPortalsData = Array.Empty<PortalData>();
        private short[] _currentIndoorRoomsIDs = Array.Empty<short>();
        private short[] _currentNeighborRoomIDs = Array.Empty<short>();
        private Transform? _listenerTransform;
        private ISpatialAudioRoom? _currentRoom;
        private Coroutine? _blendCoroutine;
        private CancellationTokenSource? _cancellationTokenSource;
        private Action? _unsubscribeReady;
        private Vector3 _previousListenerPosition;
        private bool _isDataUpdated;
        private bool _isIndoor;
        private bool _initialized;
        private bool _disposed;
        private int _currentOutdoorRoomID = -1;
        private int _currentIndoorRoomID = -1;
        private int _previousOutdoorRoomID = -1;
        private float _targetValue = 1f;
        private float _targetOutdoorHighCutFreq = 22000f;
        private float _previousOutdoorFilterValue = 1f;

        internal SystemBinding(AmbientBlendPolicy policy, EnvironmentSoundBlendSystem target)
        {
            Policy = policy;
            Target = target;
        }

        internal bool TryInitialize()
        {
            if (_disposed || _initialized) return true;
            if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated ||
                !MonoBehaviourSingleton<BetterAudio>.Instantiated ||
                !Singleton<AudioListenerConsistencyManager>.Instantiated)
            {
                WaitForServices();
                return false;
            }
            var spatial = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
            var betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (!spatial || !betterAudio || !SpatialAudioSystem.Initialized || betterAudio.Master == null)
            {
                WaitForServices();
                return false;
            }
            _listenerTransform = Singleton<AudioListenerConsistencyManager>.Instance.transform;
            if (!_listenerTransform) throw new InvalidOperationException("Ambient audio listener transform is missing: " + Policy.SourceKey);
            ResolveBlenders();
            for (var i = 0; i < Blenders.Count; i++) Blenders[i].Initialize(betterAudio.Master);
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            _previousListenerPosition = _listenerTransform.position;
            _initialized = true;
            UnsubscribeReady();
            SetInitialVolume(spatial);
            SetInitialRoom(spatial);
            SubscribeEvents();
            return true;
        }

        private void ResolveBlenders()
        {
            Blenders.Clear();
            var objects = Policy.AmbientSoundBlenders;
            if (objects == null)
                throw new InvalidOperationException("Ambient system has no serialized blender references: " + Policy.SourceKey);
            if (objects.Length == 0)
                throw new InvalidOperationException("Ambient system has no serialized blender references: " + Policy.SourceKey);
            for (var i = 0; i < objects.Length; i++)
            {
                var target = objects[i] as AmbientSoundBlender;
                if (target == null) throw new InvalidOperationException("Ambient system blender reference is not a target AmbientSoundBlender: " + Policy.SourceKey);
                var marker = target!.GetComponent<AmbientBlendPolicy>();
                if (!marker || marker.IsEnvironmentSystem)
                    throw new InvalidOperationException("Ambient target blender has no companion policy: " + target.name);
                if (ContainsTarget(target))
                    throw new InvalidOperationException("Ambient system contains a duplicate blender: " + target.name);
                if (!BlenderBindings.TryGetValue(marker, out var binding))
                {
                    binding = new BlenderBinding(marker, target);
                    BlenderBindings.Add(marker, binding);
                }
                Blenders.Add(binding);
            }
        }

        private bool ContainsTarget(AmbientSoundBlender target)
        {
            for (var i = 0; i < Blenders.Count; i++)
                if (ReferenceEquals(Blenders[i].Target, target)) return true;
            return false;
        }

        private void WaitForServices()
        {
            if (_unsubscribeReady != null) return;
            var events = GlobalEventsController.Instance;
            if (events == null) return;
            _unsubscribeReady = events.SubscribeOnEvent<SpatialAudioSystemInitializedEvent>(
                _ => TryInitialize());
        }

        private void UnsubscribeReady()
        {
            var unsubscribe = _unsubscribeReady;
            _unsubscribeReady = null;
            unsubscribe?.Invoke();
        }

        private void SubscribeEvents()
        {
            if (_unsubscribers.Count != 0) return;
            var events = GlobalEventsController.Instance;
            if (events == null) return;
            _unsubscribers.Add(events.SubscribeOnEvent<MyPlayerRoomChangedEvent>(OnPlayerRoomChanged));
            _unsubscribers.Add(events.SubscribeOnEvent<AudioPortalStateChangedEvent>(OnPortalStateChanged));
        }

        private void SetInitialVolume(SpatialAudioSystem spatial)
        {
            var room = spatial.ListenerCurrentRoom;
            if (room != null && !room.IsOutdoor)
            {
                var ambient = room.RoomAmbientData;
                var maxVolume = ambient != null ? ambient.OutdoorAmbientVolume : 1f;
                var highCut = ambient != null ? ambient.OutdoorAmbientHighCutFreq : 22000f;
                SetOutdoorMixerParams(0f, maxVolume, highCut, 0.5f, true);
                SetIndoorMixerParams(1f, 0.5f, true);
            }
            else
            {
                // Source native initialization fades the outdoor set in when
                // the listener starts outside, then writes indoor=0.
                SetOutdoorMixerParams(1f, 1f, 22000f, 0.5f, false);
                SetIndoorMixerParams(0f, 0.5f, true);
            }
        }

        private void SetInitialRoom(SpatialAudioSystem spatial)
        {
            var room = spatial.ListenerCurrentRoom;
            if (room == null || !room.IsValid) return;
            OnPlayerRoomChanged(room, room.Type, spatial.ListenerCurrentOutdoorRoomID);
        }

        private void OnPlayerRoomChanged(MyPlayerRoomChangedEvent roomChangedEvent)
            => OnPlayerRoomChanged(roomChangedEvent.Room, roomChangedEvent.CurrentRoomType,
                roomChangedEvent.CurrentOutdoorRoomID);

        private void OnPlayerRoomChanged(ISpatialAudioRoom? room, EAudioRoomTypeMask roomType, int outdoorRoomID)
        {
            _currentRoom = room;
            var incomingIndoor = room != null ? !room.IsOutdoor : roomType.IsIndoor();
            if (_isIndoor && !incomingIndoor)
            {
                _isIndoor = false;
                _currentOutdoorRoomID = outdoorRoomID;
                OnPlayerRoomChangedToOutdoor();
                return;
            }
            if (_isIndoor && incomingIndoor && _currentOutdoorRoomID == outdoorRoomID)
            {
                if (room != null)
                {
                    var ambient = room.RoomAmbientData;
                    if (ambient != null)
                    {
                        _targetValue = ambient.OutdoorAmbientVolume;
                        _targetOutdoorHighCutFreq = ambient.OutdoorAmbientHighCutFreq;
                    }
                    _currentIndoorRoomID = room.ID;
                    UpdateNeighborRoomData(room);
                    _isDataUpdated = true;
                }
                return;
            }

            _currentOutdoorRoomID = outdoorRoomID;
            _isIndoor = incomingIndoor;
            if (!_isIndoor)
            {
                _currentIndoorRoomID = -1;
                return;
            }
            if (room == null) return;
            _currentIndoorRoomID = room.ID;
            SetOutPortalsData();
            StopBlendCoroutine();
            OnPlayerRoomChangedToIndoor(room);
        }

        private void OnPlayerRoomChangedToOutdoor()
        {
            _targetValue = 1f;
            _targetOutdoorHighCutFreq = 22000f;
            StopBlendCoroutine();
            for (var i = 0; i < Blenders.Count; i++) Blenders[i].BlendToOutdoor();
            _previousOutdoorFilterValue = 1f;
            _currentIndoorRoomID = -1;
        }

        private void SetOutPortalsData()
        {
            if (_currentOutdoorRoomID >= 0 && DataProvider.TryGetData<OutdoorPortalsDataContainer>(out var dataContainer))
            {
                if (dataContainer.TryGetOutdoorPortalData(_currentOutdoorRoomID, out var portals))
                    _currentOutPortalsData = portals ?? Array.Empty<PortalData>();
                if (dataContainer.TryGetIndoorRoomsIdsByOutRoomID(_currentOutdoorRoomID, out var indoorRooms))
                    _currentIndoorRoomsIDs = indoorRooms ?? Array.Empty<short>();
            }
            _isDataUpdated = true;
        }

        private void OnPortalStateChanged(AudioPortalStateChangedEvent stateEvent)
        {
            if (stateEvent.PortalState == BaseSpatialAudioPortal.PortalState.InProgressClose ||
                stateEvent.PortalState == BaseSpatialAudioPortal.PortalState.InProgressOpen) return;
            UpdatePortalsData(stateEvent.PortalID);
            if (_currentRoom != null && _currentRoom.ID == _currentIndoorRoomID && UpdateNeighborRoomData(_currentRoom))
                _isDataUpdated = true;
        }

        private void UpdatePortalsData(int portalID)
        {
            if (_currentOutdoorRoomID < 0 || !DataProvider.TryGetData<OutdoorPortalsDataContainer>(out var dataContainer)) return;
            if (!dataContainer.TryUpdateConcretePortalData(portalID)) return;
            if (dataContainer.TryGetOutdoorPortalData(_currentOutdoorRoomID, out var portals))
                _currentOutPortalsData = portals ?? Array.Empty<PortalData>();
            _isDataUpdated = true;
        }

        private void OnPlayerRoomChangedToIndoor(ISpatialAudioRoom newRoom)
        {
            if (_listenerTransform == null) return;
            if (_previousOutdoorRoomID != _currentOutdoorRoomID && DataProvider.TryGetData<OutdoorPortalsDataContainer>(out var dataContainer))
            {
                dataContainer.FullUpdatePortalsData(_currentOutdoorRoomID);
                _previousOutdoorRoomID = _currentOutdoorRoomID;
            }
            UpdateNeighborRoomData(newRoom);
            for (var i = 0; i < Blenders.Count; i++) Blenders[i].BlendToIndoor();
            var ambient = newRoom.RoomAmbientData;
            _targetValue = ambient != null ? ambient.OutdoorAmbientVolume : 1f;
            _targetOutdoorHighCutFreq = ambient != null ? ambient.OutdoorAmbientHighCutFreq : 22000f;
            _blendCoroutine = Target.StartCoroutine(InOutBlendCoroutine(_cancellationTokenSource!.Token));
        }

        private IEnumerator InOutBlendCoroutine(CancellationToken cancellationToken)
        {
            var first = true;
            var optimalPortalData = PortalCalculatedData.Default();
            while (_isIndoor && !cancellationToken.IsCancellationRequested)
            {
                if (!IsListenerPositionIdentical() || _isDataUpdated || first)
                {
                    _isDataUpdated = false;
                    optimalPortalData = CalculateOptimalPortal(_listenerTransform!.position);
                }
                if (optimalPortalData.portalData.id != 0)
                {
                    CalculateAndApplyMixerParameters(optimalPortalData, _targetValue,
                        _targetOutdoorHighCutFreq, Time.deltaTime);
                }
                else
                {
                    for (var i = 0; i < Blenders.Count; i++)
                        Blenders[i].BlendToTargetPerFrame(_targetValue, _targetOutdoorHighCutFreq, Time.deltaTime);
                    _previousOutdoorFilterValue = _targetValue;
                }
                first = false;
                yield return null;
            }
        }

        private void CalculateAndApplyMixerParameters(PortalCalculatedData optimalPortalData,
            float maxOutdoorVolume, float maxOutdoorFreq, float dt)
        {
            var listener = _listenerTransform!.position;
            for (var i = 0; i < Blenders.Count; i++)
            {
                Blenders[i].CalculateAndApply(optimalPortalData, listener, maxOutdoorVolume,
                    maxOutdoorFreq, Policy.DepthCalculationMult, Policy.ListenerHeightWeight, dt);
            }
        }

        private bool IsListenerPositionIdentical()
        {
            var position = _listenerTransform!.position;
            if (Helpers.IsPositionIdentical(position, _previousListenerPosition, 0.2f)) return true;
            _previousListenerPosition = position;
            return false;
        }

        private void SetOutdoorMixerParams(float value, float maxOutdoorVolume, float highCutFreq,
            float targetBlendDuration, bool force)
        {
            for (var i = 0; i < Blenders.Count; i++)
                Blenders[i].SetOutdoor(value, maxOutdoorVolume, highCutFreq, targetBlendDuration, force, null);
            _previousOutdoorFilterValue = value;
        }

        private void SetIndoorMixerParams(float value, float targetBlendDuration, bool force)
        {
            for (var i = 0; i < Blenders.Count; i++)
                Blenders[i].SetIndoor(value, targetBlendDuration, force, null);
        }

        private bool TryFillOpenNeighborRoomIDs(ISpatialAudioRoom room)
        {
            var concreteRoom = room as SpatialAudioRoom;
            if (concreteRoom == null) return false;
            var portals = concreteRoom!.GetPortals();
            if (portals == null) return false;

            // The source data container stores one NeighborRoomInfo per
            // connected room and accepts that neighbor when any connecting
            // portal is in a final state.  The target removed that container
            // method, but its room/portal graph exposes the same relationship
            // through GetPortals/GetConnectedRooms.  Keep the source's
            // transition-state filter: closed and open final states both
            // represent stable graph neighbors; only in-progress states are
            // excluded.
            for (var portalIndex = 0; portalIndex < portals.Count; portalIndex++)
            {
                var portal = portals[portalIndex] as BaseSpatialAudioPortal;
                if (portal == null) continue;
                if (portal!.state == BaseSpatialAudioPortal.PortalState.InProgressClose ||
                    portal.state == BaseSpatialAudioPortal.PortalState.InProgressOpen)
                    continue;
                var connectedRooms = portal.GetConnectedRooms();
                if (connectedRooms == null) continue;
                for (var roomIndex = 0; roomIndex < connectedRooms.Count; roomIndex++)
                {
                    var neighbor = connectedRooms[roomIndex];
                    if (!neighbor || neighbor.ID == room.ID || ContainsRoom(_neighborBuffer, neighbor.ID))
                        continue;
                    _neighborBuffer.Add(neighbor.ID);
                }
            }
            return true;
        }

        private bool UpdateNeighborRoomData(ISpatialAudioRoom room)
        {
            _neighborBuffer.Clear();
            if (!TryFillOpenNeighborRoomIDs(room))
            {
                if (_currentNeighborRoomIDs.Length == 0) return false;
                _currentNeighborRoomIDs = Array.Empty<short>();
                return true;
            }
            if (NeighborListEquals(_neighborBuffer, _currentNeighborRoomIDs)) return false;
            _currentNeighborRoomIDs = new short[_neighborBuffer.Count];
            for (var i = 0; i < _neighborBuffer.Count; i++) _currentNeighborRoomIDs[i] = _neighborBuffer[i];
            return true;
        }

        private static bool NeighborListEquals(List<short> buffer, short[] current)
        {
            if (buffer.Count != current.Length) return false;
            for (var i = 0; i < current.Length; i++)
                if (buffer[i] != current[i]) return false;
            return true;
        }

        private static bool ContainsRoom(List<short> rooms, short roomID)
        {
            for (var i = 0; i < rooms.Count; i++)
                if (rooms[i] == roomID) return true;
            return false;
        }

        private PortalCalculatedData CalculateOptimalPortal(Vector3 listenerPosition)
        {
            var best = PortalCalculatedData.Default();
            var found = false;
            var bestScore = float.MaxValue;
            var bestCost = float.MaxValue;
            for (var portalIndex = 0; portalIndex < _currentOutPortalsData.Length; portalIndex++)
            {
                var portal = _currentOutPortalsData[portalIndex];
                if (portal.state == 1 || portal.state == 3) continue;
                if (portal.depth <= 0f || float.IsNaN(portal.depth) || float.IsInfinity(portal.depth)) continue;
                var distance = Vector3.Distance(listenerPosition, portal.position);
                if (distance > portal.depth * Policy.DepthCalculationMult) continue;
                if (!TryAcceptPortal(portal, out var connected, out var neighbor)) continue;
                var ratio = distance / portal.depth;
                var horizontal = SoundOcclusionUtils.CalculateHorizontalDiffraction(listenerPosition, portal.position);
                var horizontalScore = Mathf.Lerp(0f, horizontal, ratio);
                var verticalScore = Mathf.Abs(listenerPosition.y - portal.position.y) * Policy.ListenerHeightWeight;
                var score = ratio + horizontalScore + verticalScore;
                if (!connected) score += Policy.ConnectedRoomPenalty;
                if (neighbor) score += Policy.NeighborRoomPenalty;
                if (!found || score < bestScore ||
                    (Mathf.Abs(score - bestScore) < 0.05f && portal.cost < bestCost))
                {
                    found = true;
                    bestScore = score;
                    bestCost = portal.cost;
                    var distanceWeight = Policy.IgnoreDistanceRatio ? 1f : ratio;
                    var adjustedDistance = distance * (1f +
                        ((!connected ? Policy.ConnectedRoomPenalty : 0f) +
                         (neighbor ? Policy.NeighborRoomPenalty : 0f)) * distanceWeight);
                    best = new PortalCalculatedData(score, adjustedDistance, portal);
                }
            }
            return best;
        }

        private bool TryAcceptPortal(PortalData portal, out bool connected, out bool neighbor)
        {
            connected = false;
            neighbor = false;
            var start = portal.startIndex;
            var count = portal.indoorRoomsCount;
            if (start < 0 || count < 0 || start + count > _currentIndoorRoomsIDs.Length)
                throw new InvalidOperationException("Ambient portal indoor-room range is invalid: " + Policy.SourceKey);
            for (var i = 0; i < count; i++)
            {
                var roomID = _currentIndoorRoomsIDs[start + i];
                if (roomID != _currentIndoorRoomID) continue;
                if (i == 0)
                {
                    connected = true;
                    return true;
                }
                // Native checks the first (outdoor-facing) room in the
                // portal pair when the listener room is not the first entry.
                // Checking the matched room changes which portals are valid.
                if (ContainsRoom(_currentNeighborRoomIDs, _currentIndoorRoomsIDs[start]))
                {
                    neighbor = true;
                    return true;
                }
                return false;
            }
            for (var i = 0; i < count; i++)
            {
                if (!ContainsRoom(_currentNeighborRoomIDs, _currentIndoorRoomsIDs[start + i])) continue;
                neighbor = true;
                return true;
            }
            return false;
        }

        private static bool ContainsRoom(short[] rooms, short roomID)
        {
            for (var i = 0; i < rooms.Length; i++)
                if (rooms[i] == roomID) return true;
            return false;
        }

        private void StopBlendCoroutine()
        {
            if (_blendCoroutine == null) return;
            Target.StopCoroutine(_blendCoroutine);
            _blendCoroutine = null;
        }

        internal void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _initialized = false;
            UnsubscribeReady();
            for (var i = _unsubscribers.Count - 1; i >= 0; i--) _unsubscribers[i]?.Invoke();
            _unsubscribers.Clear();
            StopBlendCoroutine();
            var cancellation = _cancellationTokenSource;
            _cancellationTokenSource = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
            for (var i = 0; i < Blenders.Count; i++) Blenders[i].Dispose();
            Blenders.Clear();
        }
    }

    internal static readonly Dictionary<AmbientBlendPolicy, BlenderBinding> BlenderBindings = new();
    internal static readonly Dictionary<AmbientBlendPolicy, SystemBinding> SystemBindings = new();
    private static AmbientBlendSystemInitPatch? _initPatch;
    private static bool _enabled;
    internal static int BlenderCount => BlenderBindings.Count;
    internal static int SystemCount => SystemBindings.Count;
    internal static int Count => BlenderCount + SystemCount;

    internal static void Enable()
    {
        if (_enabled) return;
        AmbientBlendPolicy.Created += Register;
        AmbientBlendPolicy.Enabled += Retry;
        AmbientBlendPolicy.Destroyed += Remove;
        _initPatch ??= new AmbientBlendSystemInitPatch();
        _initPatch.Enable();
        _enabled = true;
    }

    internal static void Disable()
    {
        _enabled = false;
        _initPatch?.Disable();
        AmbientBlendPolicy.Created -= Register;
        AmbientBlendPolicy.Enabled -= Retry;
        AmbientBlendPolicy.Destroyed -= Remove;
        var systems = new List<SystemBinding>(SystemBindings.Values);
        for (var i = 0; i < systems.Count; i++) systems[i].Dispose();
        SystemBindings.Clear();
        var blenders = new List<BlenderBinding>(BlenderBindings.Values);
        for (var i = 0; i < blenders.Count; i++) blenders[i].Dispose();
        BlenderBindings.Clear();
    }

    private static void Register(AmbientBlendPolicy policy)
    {
        if (!policy) return;
        if (policy.IsEnvironmentSystem)
        {
            if (SystemBindings.ContainsKey(policy)) return;
            var target = policy.GetComponent<EnvironmentSoundBlendSystem>();
            if (!target) throw new InvalidOperationException("Ambient system policy has no target system: " + policy.SourceKey);
            var binding = new SystemBinding(policy, target);
            SystemBindings.Add(policy, binding);
            binding.TryInitialize();
            return;
        }
        if (BlenderBindings.ContainsKey(policy)) return;
        var blender = policy.GetComponent<AmbientSoundBlender>();
        if (!blender) throw new InvalidOperationException("Ambient blender policy has no target blender: " + policy.SourceKey);
        BlenderBindings.Add(policy, new BlenderBinding(policy, blender));
    }

    private static void Retry(AmbientBlendPolicy policy)
    {
        if (policy.IsEnvironmentSystem)
        {
            if (SystemBindings.TryGetValue(policy, out var system)) system.TryInitialize();
            else Register(policy);
        }
        else if (!BlenderBindings.ContainsKey(policy)) Register(policy);
    }

    private static void Remove(AmbientBlendPolicy policy)
    {
        if (SystemBindings.TryGetValue(policy, out var system))
        {
            SystemBindings.Remove(policy);
            system.Dispose();
        }
        if (BlenderBindings.TryGetValue(policy, out var blender))
        {
            BlenderBindings.Remove(policy);
            blender.Dispose();
        }
    }

    internal static bool TryInitializeSystem(EnvironmentSoundBlendSystem target)
    {
        if (!target || !target.TryGetComponent<AmbientBlendPolicy>(out var policy) || !policy.IsEnvironmentSystem)
            return false;
        if (!SystemBindings.TryGetValue(policy, out var binding))
        {
            Register(policy);
            binding = SystemBindings[policy];
        }
        binding.TryInitialize();
        return true;
    }
}

/// <summary>
/// The target system has the same entry point but a reduced schema and
/// scheduler.  A ModulePatch suppresses that entry point only for GameObjects
/// carrying the converted source policy; all unowned target systems retain
/// their retail initialization.
/// </summary>
internal sealed class AmbientBlendSystemInitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(EnvironmentSoundBlendSystem), nameof(EnvironmentSoundBlendSystem.Init));

    [PatchPrefix]
    private static bool Prefix(EnvironmentSoundBlendSystem __instance)
        => !AmbientBlendBindings.TryInitializeSystem(__instance);
}
