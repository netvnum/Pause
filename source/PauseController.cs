using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.UI.BattleTimer;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace Pause
{
    public class PauseController : MonoBehaviour
    {
        internal static bool IsPaused { get; private set; }

        private DateTime? _pausedDate;
        private DateTime? _unpausedDate;
        private GameTimerClass _gameTimerClass;
        private MainTimerPanel _mainTimerPanel;
        private AbstractGame _abstractGame;
        private List<AudioSource> _pausedAudioSources;

        internal static ManualLogSource Logger;

        private static GameWorld GameWorld;
        private static Player MainPlayer;
        private static FieldInfo MouseLookControlField;
        private static FieldInfo IsAimingField;
        private static FieldInfo StartTimeField;
        private static FieldInfo EscapeTimeField;
        private static FieldInfo TimerPanelField;
        private static FieldInfo GameDateTimeField;

        [UsedImplicitly]
        private void Awake()
        {
            Logger = BepInEx.Logging.Logger.CreateLogSource(nameof(PauseController));

            IsPaused = false;
            _abstractGame = Singleton<AbstractGame>.Instance;
            _mainTimerPanel = FindObjectOfType<MainTimerPanel>();
            _gameTimerClass = _abstractGame?.GameTimer;
            _pausedAudioSources = new List<AudioSource>();

            IsAimingField = AccessTools.Field(typeof(ProceduralWeaponAnimation), "_isAiming");
            StartTimeField = AccessTools.Field(typeof(GameTimerClass), "nullable_0");
            EscapeTimeField = AccessTools.Field(typeof(GameTimerClass), "nullable_1");
            TimerPanelField = AccessTools.Field(typeof(TimerPanel), "dateTime_0");
            GameDateTimeField = AccessTools.Field(typeof(GameDateTime), "_realtimeSinceStartup");
        }

        [UsedImplicitly]
        private void OnDestroy()
        {
            IsPaused = false;
            GameWorld = null;
            MainPlayer = null;
            Logger = null;
            _pausedAudioSources.Clear();
            MouseLookControlField = null;
            StartTimeField = null;
            EscapeTimeField = null;
            TimerPanelField = null;
            GameDateTimeField = null;
        }

        [UsedImplicitly]
        private void Update()
        {
            if (!IsKeyPressed(Plugin.TogglePause.Value))
            {
                return;
            }

            IsPaused = !IsPaused;

            if (IsPaused)
            {
                Pause();
            }
            else
            {
                Unpause();
                ResetFov();
            }
        }

        private void Pause()
        {
            Time.timeScale = 0f;
            _pausedDate = DateTime.UtcNow;

            MainPlayer.enabled = false;
            MainPlayer.PauseAllEffectsOnPlayer();

            foreach (var player in GetPlayers().Where(p => !p.IsYourPlayer))
            {
                Logger.LogInfo($"Deactivating player: {player.name}");
                SetPlayerState(player, false);
            }

            PauseAllAudio();
            ShowTimer();
        }

        private void Unpause()
        {
            Time.timeScale = 1f;
            _unpausedDate = DateTime.UtcNow;

            MainPlayer.enabled = true;
            MainPlayer.UnpauseAllEffectsOnPlayer();

            foreach (var player in GetPlayers().Where(p => !p.IsYourPlayer))
            {
                Logger.LogInfo($"Reactivating player: {player.name}");
                SetPlayerState(player, true);
            }

            ResumeAllAudio();
            StartCoroutine(CoHideTimer());

            UpdateTimers(GetTimePaused());
        }

        private void PauseAllAudio()
        {
            _pausedAudioSources.Clear();
            foreach (var audioSource in FindObjectsOfType<AudioSource>().Where(s => s.isPlaying))
            {
                audioSource.Pause();
                _pausedAudioSources.Add(audioSource);
            }
        }

        private void ResumeAllAudio()
        {
            foreach (var audioSource in _pausedAudioSources)
            {
                audioSource.UnPause();
            }

            _pausedAudioSources.Clear();
        }

        private static IEnumerable<Player> GetPlayers()
        {
            return GameWorld?.AllAlivePlayersList ?? new List<Player>();
        }

        private static void SetPlayerState(Player player, bool active)
        {
            if (player == null)
            {
                return;
            }

            if (player.PlayerBones != null)
            {
                foreach (var r in player.PlayerBones.GetComponentsInChildren<Rigidbody>())
                {
                    if (active)
                    {
                        r.WakeUp();
                    }
                    else
                    {
                        r.Sleep();
                    }
                }
            }

            var weaponRigidBody = player.HandsController?.ControllerGameObject?.GetComponent<Rigidbody>();
            if (weaponRigidBody != null)
            {
                weaponRigidBody.angularVelocity = Vector3.zero;
                weaponRigidBody.velocity = Vector3.zero;
                weaponRigidBody.Sleep();
            }

            if (!active)
            {
                player.AIData.BotOwner.DecisionQueue.Clear();
                player.gameObject.SetActive(false);
            }
            else
            {
                player.gameObject.SetActive(true);
                player.AIData.BotOwner.CalcGoal();
            }
        }

        private void ShowTimer()
        {
            _mainTimerPanel?.DisplayTimer();
        }

        private IEnumerator CoHideTimer()
        {
            if (_mainTimerPanel == null)
            {
                yield break;
            }

            yield return new WaitForSeconds(4f);
            _mainTimerPanel.HideTimer();
        }

        private TimeSpan GetTimePaused()
        {
            return _pausedDate.HasValue && _unpausedDate.HasValue
                ? _unpausedDate.Value - _pausedDate.Value
                : TimeSpan.Zero;
        }

        private void UpdateTimers(TimeSpan timePaused)
        {
            // Safely retrieve values using reflection.
            var startDate = StartTimeField.GetValue(_gameTimerClass) as DateTime?;
            var escapeDate = TimerPanelField.GetValue(_mainTimerPanel) as DateTime?;
            var timerPanelDate = TimerPanelField.GetValue(_mainTimerPanel) as DateTime?;
            var realTimeSinceStartup = GameDateTimeField.GetValue(GameWorld.GameDateTime) as float?;

            if (!startDate.HasValue || !escapeDate.HasValue || !timerPanelDate.HasValue || !realTimeSinceStartup.HasValue)
            {
                return;
            }

            // Adjust DateTime values.
            var adjustedStartDate = startDate.Value.Add(timePaused);
            var adjustedEscapeDate = escapeDate.Value.Add(timePaused);
            var adjustedTimerPanelDate = timerPanelDate.Value.Add(timePaused);
            var adjustedRealTime = realTimeSinceStartup.Value + (float)timePaused.TotalSeconds;

            // Set updated values back.
            StartTimeField.SetValue(_gameTimerClass, adjustedStartDate);
            EscapeTimeField.SetValue(_gameTimerClass, adjustedEscapeDate);
            TimerPanelField.SetValue(_mainTimerPanel, adjustedTimerPanelDate);
            GameDateTimeField.SetValue(GameWorld.GameDateTime, adjustedRealTime);
        }

        private static void ResetFov()
        {
            if (MainPlayer == null || MainPlayer.ProceduralWeaponAnimation == null)
            {
                return;
            }

            var baseFov = MainPlayer.ProceduralWeaponAnimation.Single_2;
            var targetFov = baseFov;

            var isAiming = (bool)(IsAimingField.GetValue(MainPlayer.ProceduralWeaponAnimation) ?? false);

            if (MainPlayer.ProceduralWeaponAnimation.PointOfView != EPointOfView.FirstPerson || MainPlayer.ProceduralWeaponAnimation.AimIndex >= MainPlayer.ProceduralWeaponAnimation.ScopeAimTransforms.Count)
            {
                return;
            }

            if (isAiming)
            {
                targetFov = MainPlayer.ProceduralWeaponAnimation.CurrentScope.IsOptic ? 35f : baseFov - 15f;
            }

            Logger.LogDebug($"Current FOV (When Unpausing): {CameraClass.Instance.Fov}, Base FOV: {baseFov}, Target FOV: {targetFov}");
            CameraClass.Instance.SetFov(targetFov, 1f, !isAiming);
        }

        internal static void Enable()
        {
            if (!Singleton<IBotGame>.Instantiated)
            {
                return;
            }

            GameWorld = Singleton<GameWorld>.Instance;
            GameWorld.GetOrAddComponent<PauseController>();
            MainPlayer = GameWorld.MainPlayer;
            Logger.LogDebug("PauseController enabled.");
        }

        internal static bool IsKeyPressed(KeyboardShortcut key)
        {
            return UnityInput.Current.GetKeyDown(key.MainKey) && key.Modifiers.All(modifier => UnityInput.Current.GetKey(modifier));
        }
    }
}
