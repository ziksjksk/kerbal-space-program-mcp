Import-Clixml: N attribute was expected. Line 776, position 12.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KspMcp
{
    internal sealed class KspMcpFlight
    {
        private readonly KspMcpCraft _craft;
        private readonly ControlLease _lease = new ControlLease();
        private Vessel _hookedVessel;
        private double _leaseUntil;
        private bool _sasEnabled;
        private bool _rcsEnabled;
        private string _stockAutopilotMode;
        private GuidancePlan _guidance;
        private double _lastGuidanceStageAt;
        private int _lastAutomaticStageCommandCursor = int.MinValue;
        private double _lastAutomaticStageCommandAt = double.NegativeInfinity;
        private int _lastAutomaticStageActivatedCursor = int.MinValue;
        private double _lastAutomaticStageActivatedAt = double.NegativeInfinity;
        private readonly HashSet<int> _automaticStageTargetsActivated = new HashSet<int>();
        private bool _haveTelemetryIdentity;
        private string _lastTelemetryVesselId;
        private string _lastTelemetrySituation;
        private string _lastTelemetryBody;
        private int _lastTelemetryStage = int.MinValue;
        private bool _lastTelemetryCommandable;
        private bool _launchHandoffPending;
        private string _lastTelemetryGuidancePhase;
        private int _lastTelemetryIgnitedEngines = -1;
        private int _lastTelemetryOperationalEngines = -1;
        private int _lastTelemetryFlameoutEngines = -1;
        private double _lastTelemetryTimeToApoapsis = double.NaN;
        private double _lastTelemetryTimeToPeriapsis = double.NaN;
        private bool _apoapsisEventArmed = true;
        private bool _periapsisEventArmed = true;
        private float _lastCompactSummaryAt = -1f;
        private string _compactSummaryVesselId;
        private Dictionary<string, object> _compactEngineSummary;
        private List<object> _compactResources;

        private sealed class GuidancePlan
        {
            public string Profile;
            public double TargetApoapsis;
            public double TargetPeriapsis;
            public double TargetAltitude;
            public double TargetLatitude;
            public double TargetLongitude;
            public bool HasLandingTarget;
            public double GearDeployAltitude;
            public bool DeployGear;
            public bool GearCommanded;
            public bool TouchdownRecorded;
            public bool FinalDescentLatched;
            public double FinalDescentEnteredAt;
            public double StartedAt;
            public double EndsAt;
            public bool AutoStage;
            public int AscentLaunchStageCursor;
            public int AscentTransferStageCursor;
            public bool CircularisationBurnStarted;
            public bool CircularisationBurnCompleted;
            public double CircularisationBurnAt;
            public double CircularisationBurnDuration;
            public double CircularisationTargetDeltaV;
            public double CircularisationStartApoapsis;
            public bool CircularisationTrimStarted;
            public double CircularisationTrimAt;
            public double CircularisationTrimDuration;
            public double CircularisationTrimTargetDeltaV;
            public bool DeorbitBurnStarted;
            public bool DeorbitBurnCompleted;
            public double IgnitionHoldUntil;
            public double IgnitionHoldThrottle;
            public int IgnitionAttempts;
            public string Phase;
            public string LastError;
            public double LastThrottle;
            public double LastPitch;
            public double LastYaw;
            public double LastTargetPitchDegrees;
            public int BurnNodeIndex;
            public double BurnUt;
            public double BurnDuration;
            public double BurnThrottle;
            public double BurnDeltaV;
            public double BurnStartAt;
            public double BurnEndAt;
            public double LastAlignmentErrorDegrees;
            public bool BurnIgnitionRecorded;
            public bool BurnCompletionRecorded;
            public double LastAvailableThrust;
            public double LastTwr;
            public double LastStoppingDistance;
            public double LastTargetDistance;
            public double LastTargetHorizontalSpeed;
            public Dictionary<string, object> Preflight;
        }

        private sealed class ControlLease
        {
            public bool HasThrottle;
            public float Throttle;
            public bool HasPitch;
            public float Pitch;
            public bool HasYaw;
            public float Yaw;
            public bool HasRoll;
            public float Roll;
            public bool HasX;
            public float X;
            public bool HasY;
            public float Y;
            public bool HasZ;
            public float Z;
            public bool HasWheelSteer;
            public float WheelSteer;
            public bool HasWheelThrottle;
            public float WheelThrottle;
            public bool HasGear;
            public bool Gear;
            public bool HasBrakes;
            public bool Brakes;
            public bool HasLights;
            public bool Lights;

            public void Clear()
            {
                HasThrottle = HasPitch = HasYaw = HasRoll = HasX = HasY = HasZ = false;
                HasWheelSteer = HasWheelThrottle = HasGear = HasBrakes = HasLights = false;
            }
        }

        public KspMcpFlight(KspMcpCraft craft)
        {
            _craft = craft;
        }

        public void Start()
        {
            // The active vessel changes on launch, staging separation, docking,
            // and vessel switching. Tick() keeps the callback attached to the
            // vessel that actually receives player controls.
        }

        public void Tick()
        {
            Vessel active = null;
            try { active = FlightGlobals.ActiveVessel; } catch (Exception) { }
            if (active != _hookedVessel)
            {
                if (_hookedVessel != null) _hookedVessel.OnFlyByWire -= OnFlyByWire;
                _hookedVessel = active;
                if (_hookedVessel != null) _hookedVessel.OnFlyByWire += OnFlyByWire;
            }
            if (_leaseUntil > 0d && Planetarium.GetUniversalTime() > _leaseUntil)
            {
                _lease.Clear();
                _leaseUntil = 0d;
            }
            if (_guidance != null)
            {
                if (active == null || !active.isCommandable)
                {
                    _guidance.LastError = active == null
                        ? "active vessel disappeared; guidance output is suspended"
                        : "active vessel is no longer commandable; guidance output is suspended";
                    _guidance.Phase = "commandability_lost";
                    _guidance.AutoStage = false;
                    _lease.Clear();
                    _leaseUntil = 0d;
                }
                string activeSituation = active == null ? null : active.situation.ToString();
                bool terminalVessel = string.Equals(activeSituation, "DEAD", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(activeSituation, "CRASHED", StringComparison.OrdinalIgnoreCase) ||
                    (active != null && active.GetTotalMass() <= 0.0001d);
                if (terminalVessel)
                {
                    _guidance.LastError = "active vessel is dead or has no remaining mass; guidance released";
                    _guidance.Phase = "vessel_lost";
                    _guidance = null;
                    _lease.Clear();
                    _leaseUntil = 0d;
                }
                else if (Planetarium.GetUniversalTime() > _guidance.EndsAt)
                {
                    _guidance.LastError = "guidance time limit reached";
                    _guidance.Phase = "guidance_timeout";
                    TryDisableStockAutopilot(active);
                    _lease.Clear();
                    _leaseUntil = 0d;
                    _guidance = null;
                }
                if (_guidance != null)
                {
                    // A landing plan owns staging only until the first actual
                    // contact.  KSP can report LANDED for a frame before the
                    // touchdown transition is recorded; guard both signals so a
                    // post-contact flameout/empty-stage check cannot fire a
                    // recovery stage and launch the vehicle off the surface.
                    bool landingContact = string.Equals(_guidance.Profile, "landing", StringComparison.OrdinalIgnoreCase) &&
                        (_guidance.TouchdownRecorded ||
                         string.Equals(activeSituation, "LANDED", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(activeSituation, "SPLASHED", StringComparison.OrdinalIgnoreCase));
                    if (landingContact)
                    {
                        _guidance.AutoStage = false;
                        TryDisableStockAutopilot(active);
                    }
                    else if (_guidance.AutoStage && Planetarium.GetUniversalTime() - _lastGuidanceStageAt > 0.25d)
                    {
                        TryAutomaticStage(active);
                    }
                }
            }
            RecordTelemetryTransitions(active);
        }

        public void Stop()
        {
            if (_hookedVessel != null) _hookedVessel.OnFlyByWire -= OnFlyByWire;
            _hookedVessel = null;
            _lease.Clear();
            _leaseUntil = 0d;
            _stockAutopilotMode = null;
            _guidance = null;
            _haveTelemetryIdentity = false;
            _lastTelemetryVesselId = null;
            _lastTelemetrySituation = null;
            _lastTelemetryBody = null;
            _lastTelemetryStage = int.MinValue;
            _lastTelemetryCommandable = false;
            _launchHandoffPending = false;
            _lastTelemetryGuidancePhase = null;
            _lastTelemetryIgnitedEngines = -1;
            _lastTelemetryOperationalEngines = -1;
            _lastTelemetryFlameoutEngines = -1;
            _lastTelemetryTimeToApoapsis = double.NaN;
            _lastTelemetryTimeToPeriapsis = double.NaN;
            _apoapsisEventArmed = true;
            _periapsisEventArmed = true;
            _lastAutomaticStageCommandCursor = int.MinValue;
            _lastAutomaticStageCommandAt = double.NegativeInfinity;
            _lastAutomaticStageActivatedCursor = int.MinValue;
            _lastAutomaticStageActivatedAt = double.NegativeInfinity;
            _automaticStageTargetsActivated.Clear();
            _lastCompactSummaryAt = -1f;
            _compactSummaryVesselId = null;
            _compactEngineSummary = null;
            _compactResources = null;
        }

        private void RecordTelemetryTransitions(Vessel vessel)
        {
            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge == null) return;
            if (vessel == null)
            {
                if (_haveTelemetryIdentity)
                {
                    bridge.RecordEvent("flight.vessel.unavailable", new Dictionary<string, object>
                    {
                        { "vessel_id", _lastTelemetryVesselId }
                    });
                }
                _haveTelemetryIdentity = false;
                _lastTelemetryVesselId = null;
                _lastTelemetrySituation = null;
                _lastTelemetryBody = null;
                _lastTelemetryStage = int.MinValue;
                _lastTelemetryCommandable = false;
                _lastTelemetryGuidancePhase = null;
                _lastTelemetryIgnitedEngines = -1;
                _lastTelemetryOperationalEngines = -1;
                _lastTelemetryFlameoutEngines = -1;
                _lastTelemetryTimeToApoapsis = double.NaN;
                _lastTelemetryTimeToPeriapsis = double.NaN;
                _apoapsisEventArmed = true;
                _periapsisEventArmed = true;
                return;
            }

            string vesselId = vessel.id.ToString();
            string situation = vessel.situation.ToString();
            string body = vessel.mainBody == null ? null : vessel.mainBody.bodyName;
            int stage = vessel.currentStage;
            bool commandable = vessel.isCommandable;
            if (!_haveTelemetryIdentity || !string.Equals(_lastTelemetryVesselId, vesselId, StringComparison.Ordinal))
            {
                bridge.RecordEvent("flight.vessel.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "vessel_name", vessel.vesselName },
                    { "body", body }
                });
                _haveTelemetryIdentity = true;
                _lastTelemetryVesselId = vesselId;
                _lastTelemetrySituation = null;
                _lastTelemetryBody = null;
                _lastTelemetryStage = int.MinValue;
                _lastTelemetryCommandable = false;
                _lastTelemetryIgnitedEngines = -1;
                _lastTelemetryOperationalEngines = -1;
                _lastTelemetryFlameoutEngines = -1;
                _lastTelemetryTimeToApoapsis = double.NaN;
                _lastTelemetryTimeToPeriapsis = double.NaN;
                _apoapsisEventArmed = true;
                _periapsisEventArmed = true;
            }
            string previousSituation = _lastTelemetrySituation;
            if (!string.Equals(_lastTelemetrySituation, situation, StringComparison.Ordinal))
            {
                bridge.RecordEvent("flight.situation.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "from", _lastTelemetrySituation },
                    { "to", situation }
                });
                _lastTelemetrySituation = situation;
            }
            if (!string.Equals(_lastTelemetryBody, body, StringComparison.Ordinal))
            {
                bridge.RecordEvent("flight.body.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "from", _lastTelemetryBody },
                    { "to", body }
                });
                _lastTelemetryBody = body;
            }
            if (_lastTelemetryStage != stage)
            {
                bridge.RecordEvent("flight.stage.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "stage", stage },
                    { "next_stage", Math.Max(0, stage - 1) }
                });
                _lastTelemetryStage = stage;
            }
            if (_lastTelemetryCommandable != commandable)
            {
                bridge.RecordEvent("flight.commandability.changed", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "commandable", commandable }
                });
                _lastTelemetryCommandable = commandable;
            }
            string guidancePhase = _guidance == null ? "inactive" : _guidance.Phase;
            if (!string.Equals(_lastTelemetryGuidancePhase, guidancePhase, StringComparison.Ordinal))
            {
                bridge.RecordEvent("flight.guidance.phase", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "phase", guidancePhase },
                    { "profile", _guidance == null ? null : _guidance.Profile }
                });
                _lastTelemetryGuidancePhase = guidancePhase;
            }

            // The compact engine cache is deliberately sampled at a lower
            // rate than the position/attitude fields, but it gives a
            // visionless client reliable edge events for ignition and
            // flameout without walking every module on every Unity frame.
            RefreshCompactSummary(vessel);
            if (_compactEngineSummary != null)
            {
                int ignited = JsonUtil.Integer(_compactEngineSummary, "ignited", 0);
                int operational = JsonUtil.Integer(_compactEngineSummary, "operational", 0);
                int flameout = JsonUtil.Integer(_compactEngineSummary, "flameout", 0);
                if (_lastTelemetryIgnitedEngines >= 0 &&
                    (ignited != _lastTelemetryIgnitedEngines || operational != _lastTelemetryOperationalEngines || flameout != _lastTelemetryFlameoutEngines))
                {
                    bridge.RecordEvent("flight.engine.state.changed", new Dictionary<string, object>
                    {
                        { "vessel_id", vesselId },
                        { "ignited", ignited },
                        { "operational", operational },
                        { "flameout", flameout },
                        { "staging_cursor", vessel.currentStage },
                        { "next_stage", Math.Max(0, vessel.currentStage - 1) }
                    });
                }
                _lastTelemetryIgnitedEngines = ignited;
                _lastTelemetryOperationalEngines = operational;
                _lastTelemetryFlameoutEngines = flameout;
            }

            bool leftPrelaunch = string.Equals(previousSituation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(situation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase);
            bool directLiftoff = leftPrelaunch &&
                !string.Equals(situation, "LANDED", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(situation, "SPLASHED", StringComparison.OrdinalIgnoreCase) &&
                (vessel.altitude > vessel.terrainAltitude + 2d || vessel.verticalSpeed > 1d || vessel.srfSpeed > 5d);
            if (string.Equals(situation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase))
            {
                _launchHandoffPending = true;
            }
            bool handoffLiftoff = _launchHandoffPending &&
                string.Equals(previousSituation, "LANDED", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(situation, "FLYING", StringComparison.OrdinalIgnoreCase) &&
                (vessel.altitude > vessel.terrainAltitude + 2d || vessel.verticalSpeed > 1d || vessel.srfSpeed > 5d);
            if (directLiftoff || handoffLiftoff)
            {
                bridge.RecordEvent("flight.liftoff", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "from", previousSituation },
                    { "to", situation },
                    { "altitude", vessel.altitude },
                    { "surface_speed", vessel.srfSpeed }
                });
                _launchHandoffPending = false;
            }
            bool touchdown = string.Equals(situation, "LANDED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(situation, "SPLASHED", StringComparison.OrdinalIgnoreCase);
            bool wasTouchdown = string.Equals(previousSituation, "LANDED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(previousSituation, "SPLASHED", StringComparison.OrdinalIgnoreCase);
            // A stock launch can briefly report PRELAUNCH -> LANDED while
            // the launch clamp/physics handoff settles. That is not a
            // landing event and must not be presented as touchdown to a
            // no-visual mission controller.
            if (touchdown && !wasTouchdown && previousSituation != null &&
                !string.Equals(previousSituation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase))
            {
                bool softTouchdown = Math.Abs(vessel.verticalSpeed) <= 8d && vessel.srfSpeed <= 8d;
                if (_guidance != null && _guidance.Profile == "landing")
                {
                    _guidance.Phase = softTouchdown ? "touchdown" : "hard_contact_recovery";
                    _guidance.LastThrottle = 0d;
                    _guidance.TouchdownRecorded = true;
                    _guidance.AutoStage = false;
                    if (!softTouchdown)
                    {
                        _guidance.LastError = "contact detected above soft-landing limits; automatic staging is locked";
                    }
                }
                bridge.RecordEvent("flight.touchdown", new Dictionary<string, object>
                {
                    { "vessel_id", vesselId },
                    { "from", previousSituation },
                    { "to", situation },
                    { "terrain_altitude", vessel.terrainAltitude },
                    { "vertical_speed", vessel.verticalSpeed },
                    { "surface_speed", vessel.srfSpeed },
                    { "soft_touchdown", softTouchdown },
                    { "contact_classification", softTouchdown ? "soft" : "hard" },
                    { "soft_velocity_limit_mps", 8d },
                    { "guidance_profile", _guidance == null ? null : _guidance.Profile }
                });
            }

            // KSP exposes a synthetic orbit while a vessel is still on the
            // launch pad. Its time-to-apsis can hover around zero and produce
            // a stream of false apoapsis/periapsis crossings. Those events
            // are only meaningful once the vessel is airborne (or already in
            // an orbit), so keep the visionless event stream quiet on the
            // pad and add hysteresis around the real crossing.
            bool orbitalEventsMeaningful = !string.Equals(situation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(situation, "LANDED", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(situation, "SPLASHED", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(situation, "DEAD", StringComparison.OrdinalIgnoreCase);
            if (vessel.orbit != null && orbitalEventsMeaningful)
            {
                double timeToApoapsis = NumberMember(vessel.orbit, "timeToAp");
                double timeToPeriapsis = NumberMember(vessel.orbit, "timeToPe");
                if (_apoapsisEventArmed && !double.IsNaN(_lastTelemetryTimeToApoapsis) &&
                    _lastTelemetryTimeToApoapsis > 0.5d && timeToApoapsis <= 0.5d)
                {
                    bridge.RecordEvent("flight.apoapsis.reached", new Dictionary<string, object>
                    {
                        { "vessel_id", vesselId },
                        { "apoapsis", NumberMember(vessel.orbit, "ApA") },
                        { "periapsis", NumberMember(vessel.orbit, "PeA") }
                    });
                    _apoapsisEventArmed = false;
                }
                else if (!_apoapsisEventArmed && timeToApoapsis > 1.0d)
                {
                    _apoapsisEventArmed = true;
                }
                if (_periapsisEventArmed && !double.IsNaN(_lastTelemetryTimeToPeriapsis) &&
                    _lastTelemetryTimeToPeriapsis > 0.5d && timeToPeriapsis <= 0.5d)
                {
                    bridge.RecordEvent("flight.periapsis.reached", new Dictionary<string, object>
                    {
                        { "vessel_id", vesselId },
                        { "apoapsis", NumberMember(vessel.orbit, "ApA") },
                        { "periapsis", NumberMember(vessel.orbit, "PeA") }
                    });
                    _periapsisEventArmed = false;
                }
                else if (!_periapsisEventArmed && timeToPeriapsis > 1.0d)
                {
                    _periapsisEventArmed = true;
                }
                _lastTelemetryTimeToApoapsis = timeToApoapsis;
                _lastTelemetryTimeToPeriapsis = timeToPeriapsis;
            }
        }

        private void OnFlyByWire(FlightCtrlState state)
        {
            if (_guidance != null)
            {
                ApplyGuidance(state);
                return;
            }
            if (_leaseUntil <= 0d || Planetarium.GetUniversalTime() > _leaseUntil) return;

            if (_lease.HasThrottle) state.mainThrottle = _lease.Throttle;
            if (_lease.HasPitch) state.pitch = _lease.Pitch;
            if (_lease.HasYaw) state.yaw = _lease.Yaw;
            if (_lease.HasRoll) state.roll = _lease.Roll;
            if (_lease.HasX) state.X = _lease.X;
            if (_lease.HasY) state.Y = _lease.Y;
            if (_lease.HasZ) state.Z = _lease.Z;
            if (_lease.HasGear)
            {
                state.gearDown = _lease.Gear;
                state.gearUp = !_lease.Gear;
            }
            if (_lease.HasLights) state.headlight = _lease.Lights;
            if (_sasEnabled) state.killRot = true;

            // Wheel and brake fields differ slightly between old KSP 1.x
            // revisions, so they are set by name when that revision exposes
            // them rather than making the whole plugin version-specific.
            if (_lease.HasWheelSteer) SetMember(state, "wheelSteer", _lease.WheelSteer);
            if (_lease.HasWheelThrottle) SetMember(state, "wheelThrottle", _lease.WheelThrottle);
            if (_lease.HasBrakes) SetMember(state, "brakes", _lease.Brakes);
        }

        public Dictionary<string, object> SetControls(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (_guidance != null) throw new KspMcpException("guidance_active", "manual controls are locked while a guidance plan is active; stop guidance first", GuidanceStatus());
            if (JsonUtil.Has(args, "throttle")) { _lease.HasThrottle = true; _lease.Throttle = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "throttle")), 0f, 1f); }
            if (JsonUtil.Has(args, "pitch")) { _lease.HasPitch = true; _lease.Pitch = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "pitch")), -1f, 1f); }
            if (JsonUtil.Has(args, "yaw")) { _lease.HasYaw = true; _lease.Yaw = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "yaw")), -1f, 1f); }
            if (JsonUtil.Has(args, "roll")) { _lease.HasRoll = true; _lease.Roll = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "roll")), -1f, 1f); }
            if (JsonUtil.Has(args, "translate_x")) { _lease.HasX = true; _lease.X = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "translate_x")), -1f, 1f); }
            if (JsonUtil.Has(args, "translate_y")) { _lease.HasY = true; _lease.Y = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "translate_y")), -1f, 1f); }
            if (JsonUtil.Has(args, "translate_z")) { _lease.HasZ = true; _lease.Z = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "translate_z")), -1f, 1f); }
            if (JsonUtil.Has(args, "wheel_steer")) { _lease.HasWheelSteer = true; _lease.WheelSteer = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "wheel_steer")), -1f, 1f); }
            if (JsonUtil.Has(args, "wheel_throttle")) { _lease.HasWheelThrottle = true; _lease.WheelThrottle = Clamp(JsonUtil.ToFloat(JsonUtil.Get(args, "wheel_throttle")), -1f, 1f); }

            if (JsonUtil.Has(args, "gear"))
            {
                _lease.HasGear = true;
                _lease.Gear = JsonUtil.Boolean(args, "gear", false);
                FireGroup("Gear", _lease.Gear);
            }
            if (JsonUtil.Has(args, "brakes"))
            {
                _lease.HasBrakes = true;
                _lease.Brakes = JsonUtil.Boolean(args, "brakes", false);
                FireGroup("Brakes", _lease.Brakes);
            }
            if (JsonUtil.Has(args, "lights"))
            {
                _lease.HasLights = true;
                _lease.Lights = JsonUtil.Boolean(args, "lights", false);
                FireGroup("Light", _lease.Lights);
            }

            double seconds = Math.Max(0.1, Math.Min(30.0, JsonUtil.Number(args, "lease_seconds", 1.5)));
            _leaseUntil = Planetarium.GetUniversalTime() + seconds;
            return new Dictionary<string, object> { { "control_lease_seconds", seconds }, { "controls", ControlSnapshot() } };
        }

        public Dictionary<string, object> StartGuidance(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (!JsonUtil.Boolean(args, "confirm", false)) throw new KspMcpException("confirmation_required", "guidance start requires confirm=true", null);
            string profile = JsonUtil.String(args, "profile", "ascent").ToLowerInvariant();
            if (profile != "ascent" && profile != "landing" && profile != "orbit" && profile != "node_burn")
            {
                throw new KspMcpException("invalid_guidance_profile", "supported profiles are ascent, orbit, landing, and node_burn", profile);
            }
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            // A freshly-created KSP vessel commonly starts with one empty
            // staging cursor above its highest engine row (for example,
            // currentStage=4 while the launch engines are inverseStage=3).
            // StartGuidance is the first no-visual command that can provide a
            // throttle request, so prepare that row before preflight instead
            // of rejecting a healthy rocket as engine-less or letting it fall
            // from the launch pad with zero throttle.
            int ascentLaunchStageCursor = profile == "ascent" ? DetectAscentLaunchStageCursor(activeVessel) : int.MinValue;
            PrepareLaunchStageForGuidance(profile, activeVessel);
            Dictionary<string, object> preflight = GuidancePreflight(profile);
            bool hasLandingTarget = JsonUtil.Has(args, "target_latitude") || JsonUtil.Has(args, "target_longitude");
            if (hasLandingTarget && profile != "landing")
            {
                throw new KspMcpException("invalid_guidance_target", "target_latitude/target_longitude are only valid for landing guidance", null);
            }
            double targetLatitude = JsonUtil.Number(args, "target_latitude", 0d);
            double targetLongitude = JsonUtil.Number(args, "target_longitude", 0d);
            if (hasLandingTarget && (targetLatitude < -90d || targetLatitude > 90d || targetLongitude < -180d || targetLongitude > 180d))
            {
                throw new KspMcpException("invalid_guidance_target", "landing target latitude must be -90..90 and longitude must be -180..180", null);
            }
            double now = Planetarium.GetUniversalTime();
            // A Mun landing must be able to wait through the deorbit coast and
            // the long ballistic descent at real time.  The no-visual control
            // loop deliberately forbids time warp while it owns the vessel,
            // so a 600-second default can release a healthy lander before the
            // powered-descent window.  Keep the explicit caller limit intact,
            // but make the safe default long enough for a normal Mun approach.
            double maxSeconds = Math.Max(5d, Math.Min(3600d, JsonUtil.Number(args, "max_seconds", profile == "landing" || profile == "node_burn" ? 1800d : 240d)));
            _guidance = new GuidancePlan
            {
                Profile = profile,
                TargetApoapsis = Math.Max(1000d, JsonUtil.Number(args, "target_apoapsis", 80000d)),
                TargetPeriapsis = JsonUtil.Number(args, "target_periapsis", 75000d),
                TargetAltitude = Math.Max(10d, JsonUtil.Number(args, "target_altitude", 0d)),
                TargetLatitude = targetLatitude,
                TargetLongitude = targetLongitude,
                HasLandingTarget = hasLandingTarget,
                GearDeployAltitude = Math.Max(100d, Math.Min(10000d, JsonUtil.Number(args, "gear_deploy_altitude", 2500d))),
                DeployGear = JsonUtil.Boolean(args, "deploy_gear", profile == "landing"),
                GearCommanded = false,
                TouchdownRecorded = false,
                FinalDescentLatched = false,
                FinalDescentEnteredAt = 0d,
                StartedAt = now,
                EndsAt = now + maxSeconds,
                AutoStage = JsonUtil.Boolean(args, "auto_stage", true),
                AscentLaunchStageCursor = ascentLaunchStageCursor,
                AscentTransferStageCursor = profile == "ascent" ? DetectAscentTransferStageCursor(activeVessel, ascentLaunchStageCursor) : int.MinValue,
                CircularisationBurnStarted = false,
                CircularisationBurnCompleted = false,
                CircularisationBurnAt = 0d,
                CircularisationBurnDuration = 0d,
                CircularisationTargetDeltaV = 0d,
                CircularisationStartApoapsis = 0d,
                CircularisationTrimStarted = false,
                CircularisationTrimAt = 0d,
                CircularisationTrimDuration = 0d,
                CircularisationTrimTargetDeltaV = 0d,
                DeorbitBurnStarted = false,
                DeorbitBurnCompleted = false,
                IgnitionHoldUntil = 0d,
                IgnitionHoldThrottle = 0d,
                IgnitionAttempts = 0,
                Phase = "initialising",
                LastError = null,
                BurnCompletionRecorded = false,
                BurnIgnitionRecorded = false,
                LastAvailableThrust = JsonUtil.Number(preflight, "available_thrust_kN", 0d),
                LastTwr = JsonUtil.Number(preflight, "twr", 0d),
                Preflight = preflight
            };
            try
            {
                if (profile == "node_burn") ConfigureNodeBurn(args, now);
            }
            catch
            {
                // Do not leave a half-configured controller active when a
                // node, engine, or timing precondition fails.
                _guidance = null;
                throw;
            }
            _lease.Clear();
            _leaseUntil = 0d;
            // Stock SAS is a useful second control loop for large stacks: the
            // MCP still supplies the target direction and throttle, while
            // KSP handles the high-inertia reaction-wheel/gimbal damping.
            // Landing and node-burn profiles retain the direct controller,
            // because they need precise retrograde/burn-vector alignment.
            _sasEnabled = profile == "ascent" || profile == "orbit";
            bool sasActionGroupApplied = SetVesselActionGroupState(activeVessel, "SAS", _sasEnabled);
            // SetGroup updates the saved action-group state, but on this KSP
            // build it does not drive the live FlightCtrlState SAS switch.
            // Fire the action as well so the stock autopilot actually owns
            // the attitude loop in the running flight scene.
            try { FireGroup("SAS", _sasEnabled); }
            catch (Exception exception)
            {
                if (!sasActionGroupApplied) KspMcpBridge.Log("could not fire SAS action group: " + exception.Message);
            }
            _lastGuidanceStageAt = now;
            _lastAutomaticStageCommandCursor = int.MinValue;
            _lastAutomaticStageCommandAt = double.NegativeInfinity;
            _lastAutomaticStageActivatedCursor = int.MinValue;
            _lastAutomaticStageActivatedAt = double.NegativeInfinity;
            _automaticStageTargetsActivated.Clear();
            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge != null) bridge.RecordEvent("flight.guidance.started", new Dictionary<string, object>
            {
                { "vessel_id", activeVessel == null ? null : activeVessel.id.ToString() },
                { "vessel_name", activeVessel == null ? null : activeVessel.vesselName },
                { "situation", activeVessel == null ? null : activeVessel.situation.ToString() },
                { "profile", profile },
                { "preflight", preflight }
            });
            return GuidanceStatus();
        }

        private static Dictionary<string, object> GuidancePreflight(string profile)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) throw new KspMcpException("no_active_vessel", "KSP has no active vessel", null);
            if (!vessel.isCommandable)
            {
                throw new KspMcpException("not_commandable", "the active vessel is not commandable; guidance is unsafe", vessel.vesselName);
            }

            double mass = Math.Max(0d, vessel.GetTotalMass());
            double thrust = AvailableGuidanceThrust(vessel);
            double altitude = Math.Max(0d, vessel.altitude);
            double gravity = SurfaceGravity(vessel, altitude);
            double twr = mass <= 0d || gravity <= 0d ? 0d : thrust / (mass * gravity);
            string situation = vessel.situation.ToString();
            bool onPad = string.Equals(situation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(situation, "LANDED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(situation, "SPLASHED", StringComparison.OrdinalIgnoreCase);
            if (thrust <= 0d || mass <= 0d)
            {
                throw new KspMcpException("guidance_engine_unavailable", "guidance requires a positive vessel mass and usable staged engine thrust", null);
            }
            if ((profile == "ascent" || profile == "orbit") && onPad && twr < 1.02d)
            {
                throw new KspMcpException("guidance_twr_too_low", "the active vessel cannot safely lift off at the current staging/throttle configuration", new Dictionary<string, object>
                {
                    { "twr", twr },
                    { "mass_tonnes", mass },
                    { "thrust_kN", thrust },
                    { "gravity_mps2", gravity },
                    { "current_stage", vessel.currentStage },
                    { "next_stage", Math.Max(0, vessel.currentStage - 1) }
                });
            }

            Dictionary<string, object> engines = EngineSummary(vessel);
            return new Dictionary<string, object>
            {
                { "profile", profile },
                { "vessel_name", vessel.vesselName },
                { "situation", situation },
                { "commandable", vessel.isCommandable },
                { "mass_tonnes", mass },
                { "available_thrust_kN", thrust },
                { "local_gravity_mps2", gravity },
                { "twr", twr },
                { "current_stage", vessel.currentStage },
                { "next_stage", Math.Max(0, vessel.currentStage - 1) },
                { "engine_count", engines["count"] },
                { "stage_report", GuidanceStageReport(vessel) }
            };
        }

        public Dictionary<string, object> StopGuidance()
        {
            bool wasActive = _guidance != null;
            _guidance = null;
            _lease.Clear();
            _leaseUntil = 0d;
            TryDisableStockAutopilot(FlightGlobals.ActiveVessel);
            if (_sasEnabled)
            {
                _sasEnabled = false;
                if (!SetVesselActionGroupState(FlightGlobals.ActiveVessel, "SAS", false))
                {
                    try { FireGroup("SAS", false); } catch (Exception) { }
                }
            }
            return new Dictionary<string, object> { { "stopped", wasActive }, { "guidance", GuidanceStatus() } };
        }

        public Dictionary<string, object> UpdateGuidance(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (_guidance == null) throw new KspMcpException("guidance_inactive", "there is no active guidance plan to update", null);

            if (JsonUtil.Has(args, "target_apoapsis"))
            {
                _guidance.TargetApoapsis = Math.Max(1000d, JsonUtil.Number(args, "target_apoapsis", _guidance.TargetApoapsis));
            }
            if (JsonUtil.Has(args, "target_periapsis"))
            {
                _guidance.TargetPeriapsis = JsonUtil.Number(args, "target_periapsis", _guidance.TargetPeriapsis);
            }
            if (JsonUtil.Has(args, "target_altitude"))
            {
                _guidance.TargetAltitude = Math.Max(10d, JsonUtil.Number(args, "target_altitude", _guidance.TargetAltitude));
            }
            if (JsonUtil.Has(args, "auto_stage"))
            {
                _guidance.AutoStage = JsonUtil.Boolean(args, "auto_stage", _guidance.AutoStage);
            }
            if (JsonUtil.Has(args, "deploy_gear"))
            {
                _guidance.DeployGear = JsonUtil.Boolean(args, "deploy_gear", _guidance.DeployGear);
            }
            if (JsonUtil.Has(args, "gear_deploy_altitude"))
            {
                _guidance.GearDeployAltitude = Math.Max(100d, Math.Min(10000d, JsonUtil.Number(args, "gear_deploy_altitude", _guidance.GearDeployAltitude)));
            }
            if (JsonUtil.Has(args, "target_latitude") || JsonUtil.Has(args, "target_longitude"))
            {
                if (_guidance.Profile != "landing") throw new KspMcpException("invalid_guidance_target", "landing coordinates can only update a landing plan", null);
                double latitude = JsonUtil.Number(args, "target_latitude", _guidance.TargetLatitude);
                double longitude = JsonUtil.Number(args, "target_longitude", _guidance.TargetLongitude);
                if (latitude < -90d || latitude > 90d || longitude < -180d || longitude > 180d)
                {
                    throw new KspMcpException("invalid_guidance_target", "landing target latitude must be -90..90 and longitude must be -180..180", null);
                }
                _guidance.TargetLatitude = latitude;
                _guidance.TargetLongitude = longitude;
                _guidance.HasLandingTarget = true;
            }
            if (JsonUtil.Has(args, "extend_seconds"))
            {
                double extension = Math.Max(0d, Math.Min(3600d, JsonUtil.Number(args, "extend_seconds", 0d)));
                _guidance.EndsAt = Math.Min(_guidance.StartedAt + 7200d, _guidance.EndsAt + extension);
            }

            Vessel eventVessel = FlightGlobals.ActiveVessel;
            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge != null) bridge.RecordEvent("flight.guidance.updated", new Dictionary<string, object>
            {
                { "vessel_id", eventVessel == null ? null : eventVessel.id.ToString() },
                { "vessel_name", eventVessel == null ? null : eventVessel.vesselName },
                { "profile", _guidance.Profile },
                { "target_apoapsis", _guidance.TargetApoapsis },
                { "target_periapsis", _guidance.TargetPeriapsis },
                { "target_altitude", _guidance.TargetAltitude },
                { "has_landing_target", _guidance.HasLandingTarget },
                { "target_latitude", _guidance.HasLandingTarget ? (object)_guidance.TargetLatitude : null },
                { "target_longitude", _guidance.HasLandingTarget ? (object)_guidance.TargetLongitude : null },
                { "auto_stage", _guidance.AutoStage },
                { "deploy_gear", _guidance.DeployGear }
            });
            return GuidanceStatus();
        }

        public Dictionary<string, object> GuidanceStatus()
        {
            if (_guidance == null) return new Dictionary<string, object> { { "active", false } };
            double now = Planetarium.GetUniversalTime();
            Vessel activeVessel = null;
            object autopilot = null;
            object stockSas = null;
            try
            {
                activeVessel = FlightGlobals.ActiveVessel;
                if (activeVessel != null && activeVessel.Autopilot != null)
                {
                    autopilot = activeVessel.Autopilot;
                    stockSas = activeVessel.Autopilot.SAS;
                }
            }
            catch (Exception) { }
            return new Dictionary<string, object>
            {
                { "active", true },
                { "profile", _guidance.Profile },
                { "phase", _guidance.Phase },
                { "target_apoapsis", _guidance.TargetApoapsis },
                { "target_periapsis", _guidance.TargetPeriapsis },
                { "target_altitude", _guidance.TargetAltitude },
                { "target_latitude", _guidance.HasLandingTarget ? (object)_guidance.TargetLatitude : null },
                { "target_longitude", _guidance.HasLandingTarget ? (object)_guidance.TargetLongitude : null },
                { "has_landing_target", _guidance.HasLandingTarget },
                { "deploy_gear", _guidance.DeployGear },
                { "gear_deploy_altitude", _guidance.GearDeployAltitude },
                { "gear_commanded", _guidance.GearCommanded },
                { "touchdown_recorded", _guidance.TouchdownRecorded },
                { "final_descent_active", _guidance.FinalDescentLatched },
                { "final_descent_entered_at", _guidance.FinalDescentEnteredAt },
                { "auto_stage", _guidance.AutoStage },
                { "circularisation_burn_started", _guidance.CircularisationBurnStarted },
                { "circularisation_burn_completed", _guidance.CircularisationBurnCompleted },
                { "ascent_launch_stage_cursor", _guidance.AscentLaunchStageCursor == int.MinValue ? (object)null : _guidance.AscentLaunchStageCursor },
                { "ascent_transfer_stage_cursor", _guidance.AscentTransferStageCursor == int.MinValue ? (object)null : _guidance.AscentTransferStageCursor },
                { "transfer_stage_active", IsAscentTransferStageActive(FlightGlobals.ActiveVessel) },
                { "circularisation_burn_elapsed_seconds", _guidance.CircularisationBurnStarted ? Math.Max(0d, now - _guidance.CircularisationBurnAt) : 0d },
                { "circularisation_burn_duration_seconds", _guidance.CircularisationBurnDuration },
                { "circularisation_burn_remaining_seconds", _guidance.CircularisationBurnStarted && !_guidance.CircularisationBurnCompleted ? Math.Max(0d, _guidance.CircularisationBurnDuration - Math.Max(0d, now - _guidance.CircularisationBurnAt)) : 0d },
                { "circularisation_target_delta_v_mps", _guidance.CircularisationTargetDeltaV },
                { "circularisation_start_apoapsis_m", _guidance.CircularisationStartApoapsis },
                { "circularisation_trim_started", _guidance.CircularisationTrimStarted },
                { "circularisation_trim_elapsed_seconds", _guidance.CircularisationTrimStarted ? Math.Max(0d, now - _guidance.CircularisationTrimAt) : 0d },
                { "circularisation_trim_duration_seconds", _guidance.CircularisationTrimDuration },
                { "circularisation_trim_remaining_seconds", _guidance.CircularisationTrimStarted && !_guidance.CircularisationBurnCompleted ? Math.Max(0d, _guidance.CircularisationTrimDuration - Math.Max(0d, now - _guidance.CircularisationTrimAt)) : 0d },
                { "circularisation_trim_target_delta_v_mps", _guidance.CircularisationTrimTargetDeltaV },
                { "deorbit_burn_started", _guidance.DeorbitBurnStarted },
                { "deorbit_burn_completed", _guidance.DeorbitBurnCompleted },
                { "ignition_hold_active", _guidance.IgnitionHoldUntil > now },
                { "ignition_hold_remaining_seconds", Math.Max(0d, _guidance.IgnitionHoldUntil - now) },
                { "ignition_hold_throttle", _guidance.IgnitionHoldThrottle },
                { "ignition_attempts", _guidance.IgnitionAttempts },
                { "seconds_remaining", Math.Max(0d, _guidance.EndsAt - now) },
                { "last_throttle", _guidance.LastThrottle },
                { "last_pitch", _guidance.LastPitch },
                { "last_yaw", _guidance.LastYaw },
                { "target_pitch_degrees", _guidance.LastTargetPitchDegrees },
                { "burn_node_index", _guidance.BurnNodeIndex },
                { "burn_ut", _guidance.BurnUt },
                { "burn_duration", _guidance.BurnDuration },
                { "burn_throttle", _guidance.BurnThrottle },
                { "burn_delta_v", _guidance.BurnDeltaV },
                { "burn_start_ut", _guidance.BurnStartAt },
                { "burn_end_ut", _guidance.BurnEndAt },
                { "burn_alignment_error_degrees", _guidance.LastAlignmentErrorDegrees },
                { "stock_autopilot_mode", _stockAutopilotMode },
                { "stock_autopilot_enabled", autopilot == null ? (object)null : BoolMember(autopilot, "Enabled") },
                { "stock_autopilot_mode_actual", autopilot == null ? null : TextMember(autopilot, "Mode") },
                { "stock_sas_can_engage", stockSas == null ? (object)null : InvokeBoolMethod(stockSas, "CanEngageSAS") },
                { "stock_sas_fbw_connected", stockSas == null ? (object)null : BoolMember(stockSas, "FBWconnected") },
                { "burn_ignition_recorded", _guidance.BurnIgnitionRecorded },
                { "available_thrust_kN", _guidance.LastAvailableThrust },
                { "twr", _guidance.LastTwr },
                { "stopping_distance_m", _guidance.LastStoppingDistance },
                { "target_distance_m", _guidance.LastTargetDistance },
                { "target_horizontal_speed_mps", _guidance.LastTargetHorizontalSpeed },
                { "last_error", _guidance.LastError },
                { "preflight", _guidance.Preflight }
            };
        }

        private void ApplyGuidance(FlightCtrlState state)
        {
            Vessel vessel = _hookedVessel;
            if (vessel == null) return;
            if (!vessel.isCommandable) return;
            if (_guidance.Profile == "landing")
            {
                ApplyLandingGuidance(state, vessel);
            }
            else if (_guidance.Profile == "node_burn")
            {
                ApplyNodeBurnGuidance(state, vessel);
            }
            else if (_guidance.Profile == "orbit")
            {
                ApplyOrbitGuidance(state, vessel);
            }
            else
            {
                ApplyAscentGuidance(state, vessel);
            }
        }

        private void ApplyAscentGuidance(FlightCtrlState state, Vessel vessel)
        {
            double altitude = Math.Max(0d, vessel.altitude);
            double apoapsis = vessel.orbit == null ? 0d : NumberMember(vessel.orbit, "ApA");
            double periapsis = vessel.orbit == null ? -1d : NumberMember(vessel.orbit, "PeA");
            double timeToApoapsis = vessel.orbit == null ? double.PositiveInfinity : NumberMember(vessel.orbit, "timeToAp");
            double apoapsisTolerance = Math.Max(500d, Math.Max(1d, _guidance.TargetApoapsis) * 0.02d);
            double periapsisTolerance = Math.Max(250d, Math.Max(1d, _guidance.TargetPeriapsis) * 0.01d);
            bool needsCircularisation = _guidance.TargetPeriapsis > 0d &&
                periapsis < _guidance.TargetPeriapsis - periapsisTolerance;
            bool transferStageActive = IsAscentTransferStageActive(vessel);
            // Once a finite circularisation impulse has been latched, keep
            // that state machine in control until its time/PeA stop condition
            // fires.  ApA can dip a few metres below the insertion threshold
            // on the first physics tick after apoapsis; allowing the normal
            // apoapsis-trim branch to win there changes a timed full-throttle
            // burn into a low-throttle feedback burn and can run it away.
            bool circularisationBurnInProgress = _guidance.CircularisationBurnStarted &&
                !_guidance.CircularisationBurnCompleted;
            bool apoapsisAtInsertionWindow = _guidance.TargetApoapsis > 0d &&
                apoapsis >= _guidance.TargetApoapsis - apoapsisTolerance;
            // Stock KSP can restore the pre-separation staging cursor on the
            // first physics tick after a decoupler fires.  The transfer
            // engine is nevertheless the physically active engine, so the
            // guidance state must follow the successful activation record,
            // not only Vessel.currentStage.  Otherwise the controller stays
            // in coast_to_apoapsis forever and misses circularisation.
            bool transferBurnWindowMissed = transferStageActive &&
                apoapsisAtInsertionWindow &&
                vessel.verticalSpeed < -5d &&
                altitude >= Math.Max(1000d, _guidance.TargetApoapsis - 15000d);
            // Keep the early gravity turn shallow.  A frame-driven controller
            // must tolerate the launch-pad handoff, flexible joints, and the
            // stock reference transform settling; commanding a 20-degree
            // turn by 5 km made the large test vehicle dive before the upper
            // stage could be reached.  Later phases still converge toward a
            // near-horizontal orbital attitude once the air is thin.
            double targetPitch = 90d;
            if (altitude >= 8000d && altitude < 20000d) targetPitch = 85d - (altitude - 8000d) / 12000d * 15d;
            else if (altitude >= 20000d && altitude < 35000d) targetPitch = 70d - (altitude - 20000d) / 15000d * 25d;
            else if (altitude >= 35000d) targetPitch = 30d;

            Vector3d target = AscentTargetVector(vessel, targetPitch);
            // The target vector is deliberately conservative, but a large
            // editor-created stack can still lose vertical velocity while
            // its reference transform settles or a flexible joint oscillates.
            // If that happens below the upper atmosphere, prioritise altitude
            // over horizontal efficiency: point back at the local normal and
            // use full thrust until the climb is healthy again.  This is also
            // the hand-off a human pilot would make when the rocket begins to
            // fall, and prevents the no-visual controller from steering a
            // recoverable ascent into terrain or water.
            double targetAlignmentError = DirectionErrorDegrees(vessel, target);
            bool verticalRecovery = altitude < 18000d &&
                (vessel.verticalSpeed < 80d || targetAlignmentError > 35d);
            if (verticalRecovery && !apoapsisAtInsertionWindow && !circularisationBurnInProgress)
            {
                target = SurfaceNormal(vessel);
                targetPitch = 90d;
            }
            double throttle = 1d;
            if (apoapsisAtInsertionWindow || circularisationBurnInProgress)
            {
                target = vessel.obt_velocity.sqrMagnitude > 0.01d ? vessel.obt_velocity.normalized : target;
                // The launch stage must first coast with zero throttle. The
                // automatic stage controller separates it before the apoapsis
                // window and only the explicitly selected transfer stage may
                // perform this insertion burn.
                if (!transferStageActive && !circularisationBurnInProgress)
                {
                    throttle = 0d;
                    _guidance.Phase = needsCircularisation ? "coast_to_apoapsis" : "orbit_achieved";
                }
                else if (!needsCircularisation && !circularisationBurnInProgress)
                {
                    throttle = 0d;
                    _guidance.CircularisationBurnCompleted = true;
                    _guidance.Phase = "orbit_achieved";
                }
                else if (_guidance.CircularisationBurnCompleted)
                {
                    throttle = 0d;
                    _guidance.Phase = "circularisation_coast";
                }
                else
                {
                    // Begin early enough to account for a realistic finite
                    // burn. Waiting until timeToAp <= 5 s starts far too late
                    // for a 650 kN transfer engine and forces the burn to
                    // continue well past apoapsis.
                    double estimatedTargetDeltaV;
                    double estimatedBurnDuration = EstimateCircularisationBurnDuration(
                        vessel,
                        _guidance.TargetApoapsis,
                        _guidance.TargetPeriapsis,
                        out estimatedTargetDeltaV);
                    // Center a finite prograde impulse on apoapsis. KSP's
                    // currentStage is the next staging cursor, so the
                    // transfer engine can be selected before the burn starts;
                    // the old fixed 60 s gate often ignited a 50-60 s burn
                    // more than 40 s before apoapsis.
                    double burnLeadTime = estimatedBurnDuration > 0d
                        ? Math.Max(5d, estimatedBurnDuration * 0.5d)
                        : 5d;
                    if (!_guidance.CircularisationBurnStarted &&
                        (timeToApoapsis <= Math.Min(60d, burnLeadTime) || transferBurnWindowMissed))
                    {
                        _guidance.CircularisationBurnDuration = estimatedBurnDuration;
                        _guidance.CircularisationTargetDeltaV = estimatedTargetDeltaV;
                        _guidance.CircularisationBurnAt = Planetarium.GetUniversalTime();
                        _guidance.CircularisationStartApoapsis = apoapsis;
                        _guidance.CircularisationBurnStarted = true;
                    }

                    if (_guidance.CircularisationBurnStarted)
                    {
                        double burnElapsed = Math.Max(0d, Planetarium.GetUniversalTime() - _guidance.CircularisationBurnAt);
                        bool periapsisReached = periapsis >= _guidance.TargetPeriapsis - periapsisTolerance;
                        bool burnTimeReached = _guidance.CircularisationBurnDuration > 0d &&
                            burnElapsed >= _guidance.CircularisationBurnDuration;
                        if (periapsisReached)
                        {
                            throttle = 0d;
                            _guidance.CircularisationBurnCompleted = true;
                            _guidance.Phase = "orbit_achieved";
                        }
                        else if (_guidance.CircularisationTrimStarted)
                        {
                            double trimElapsed = Math.Max(0d, Planetarium.GetUniversalTime() - _guidance.CircularisationTrimAt);
                            bool trimTimeReached = _guidance.CircularisationTrimDuration > 0d &&
                                trimElapsed >= _guidance.CircularisationTrimDuration;
                            if (trimTimeReached)
                            {
                                throttle = 0d;
                                _guidance.CircularisationBurnCompleted = true;
                                _guidance.Phase = "circularisation_coast";
                            }
                            else
                            {
                                throttle = 1d;
                                _guidance.Phase = "circularisation_trim";
                            }
                        }
                        else if (burnTimeReached)
                        {
                            // A timed burn is only an initial estimate. If the
                            // measured PeA is still below target, use one short
                            // feedback trim while the vessel is still close to
                            // the current apoapsis. This compensates for thrust
                            // curves, steering lag, and the mass change during
                            // the primary burn without allowing a later orbit
                            // to restart the burn loop.
                            double trimDeltaV;
                            double trimDuration = EstimateFiniteBurnDuration(
                                vessel,
                                EstimateCircularisationResidualDeltaV(vessel, _guidance.TargetApoapsis, _guidance.TargetPeriapsis, out trimDeltaV));
                            bool nearApoapsis = IsNearApoapsisForTrim(vessel, _guidance.TargetApoapsis, apoapsisTolerance);
                            if (nearApoapsis && trimDeltaV > 0.5d && trimDuration > 0d)
                            {
                                _guidance.CircularisationTrimStarted = true;
                                _guidance.CircularisationTrimAt = Planetarium.GetUniversalTime();
                                _guidance.CircularisationTrimDuration = trimDuration;
                                _guidance.CircularisationTrimTargetDeltaV = trimDeltaV;
                                throttle = 1d;
                                _guidance.Phase = "circularisation_trim";
                            }
                            else
                            {
                                throttle = 0d;
                                _guidance.CircularisationBurnCompleted = true;
                                _guidance.Phase = "circularisation_coast";
                            }
                        }
                        else
                        {
                            // This is a timed prograde impulse. Do not keep
                            // recalculating throttle from a lagging PeA after
                            // the vehicle has passed apoapsis; that was the
                            // runaway condition seen in the previous run.
                            throttle = 1d;
                            _guidance.Phase = "circularisation_burn";
                        }
                    }
                    else
                    {
                        throttle = 0d;
                        _guidance.Phase = "coast_to_apoapsis";
                    }
                }
            }
            else if (apoapsis > _guidance.TargetApoapsis * 0.85d)
            {
                throttle = ClampDouble((_guidance.TargetApoapsis - apoapsis) / Math.Max(1d, _guidance.TargetApoapsis * 0.15d), 0.15d, 1d);
                _guidance.Phase = "apoapsis_trim";
            }
            else if (altitude < 500d) _guidance.Phase = "vertical_rise";
            else if (altitude < 35000d) _guidance.Phase = "gravity_turn";
            else _guidance.Phase = "orbital_ascent";

            if (throttle > 0d)
            {
                double mass = Math.Max(0.001d, vessel.GetTotalMass());
                double thrust = AvailableGuidanceThrust(vessel);
                double gravity = SurfaceGravity(vessel, altitude);
                if (thrust > 0d && gravity > 0d)
                {
                    // Hold a realistic early-ascent TWR instead of applying
                    // full throttle to an unusually high-TWR stack. The
                    // apoapsis error above remains the stricter limiter near
                    // the target orbit.
                    double targetTwr;
                    if (altitude < 1000d) targetTwr = 1.35d;
                    else if (altitude < 10000d) targetTwr = 1.55d;
                    else if (altitude < 25000d) targetTwr = 1.85d;
                    else if (altitude < 40000d) targetTwr = 2.05d;
                    else targetTwr = 2.20d;
                    double twrThrottle = targetTwr * mass * gravity / thrust;
                    throttle = Math.Min(throttle, ClampDouble(twrThrottle, 0.15d, 1d));
                }
                if (verticalRecovery && !apoapsisAtInsertionWindow && !circularisationBurnInProgress)
                {
                    throttle = vessel.verticalSpeed < 0d ? 1d : Math.Max(throttle, 0.9d);
                    _guidance.Phase = "vertical_recovery";
                }
            }

            ApplyDirectionControl(state, vessel, target, throttle, targetPitch);
        }

        private void ConfigureNodeBurn(Dictionary<string, object> args, double now)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            ManeuverNode node = FindManeuverNode(vessel, Math.Max(0, JsonUtil.Integer(args, "node_index", 0)));
            if (node == null) throw new KspMcpException("maneuver_not_found", "requested maneuver node does not exist", null);
            double deltaV = node.DeltaV.magnitude;
            if (deltaV < 0.01d) throw new KspMcpException("maneuver_empty", "requested maneuver node has near-zero delta-v", null);
            double throttle = ClampDouble(JsonUtil.Number(args, "throttle", 1d), 0.1d, 1d);
            double thrust = AvailableGuidanceThrust(vessel);
            double mass = vessel.GetTotalMass();
            if (thrust <= 0d || mass <= 0d)
            {
                throw new KspMcpException("burn_engine_unavailable", "cannot estimate a burn without an active engine and positive vessel mass", null);
            }
            double acceleration = thrust / mass;
            double duration = Math.Max(0.5d, Math.Min(600d, deltaV / Math.Max(0.01d, acceleration * throttle) * 1.15d));
            if (node.UT < now - duration * 0.5d)
            {
                throw new KspMcpException("maneuver_in_past", "requested maneuver node is already too far in the past", new Dictionary<string, object> { { "ut", node.UT }, { "now", now } });
            }
            _guidance.BurnNodeIndex = Math.Max(0, JsonUtil.Integer(args, "node_index", 0));
            _guidance.BurnUt = node.UT;
            _guidance.BurnDuration = duration;
            _guidance.BurnThrottle = throttle;
            _guidance.BurnDeltaV = deltaV;
            _guidance.BurnStartAt = node.UT - duration * 0.5d;
            _guidance.BurnEndAt = node.UT + duration * 0.5d;
            _guidance.AutoStage = false;
            _guidance.Phase = "coast_to_node_burn";
            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge != null)
            {
                bridge.RecordEvent("flight.maneuver_burn.started", new Dictionary<string, object>
                {
                    { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                    { "vessel_name", vessel == null ? null : vessel.vesselName },
                    { "node_index", _guidance.BurnNodeIndex },
                    { "ut", _guidance.BurnUt },
                    { "delta_v", _guidance.BurnDeltaV },
                    { "duration", _guidance.BurnDuration }
                });
            }
        }

        private void ApplyNodeBurnGuidance(FlightCtrlState state, Vessel vessel)
        {
            ManeuverNode node = FindManeuverNode(vessel, _guidance.BurnNodeIndex);
            if (node == null)
            {
                _guidance.LastError = "maneuver node disappeared before burn completion";
                _guidance.Phase = "node_missing";
                ApplyDirectionControl(state, vessel, vessel.obt_velocity, 0d, 0d);
                return;
            }
            Vector3d burnVector = vessel.orbit == null ? node.DeltaV : node.GetBurnVector(vessel.orbit);
            if (burnVector.sqrMagnitude < 0.0001d)
            {
                _guidance.LastError = "maneuver burn vector is empty";
                _guidance.Phase = "node_empty";
                ApplyDirectionControl(state, vessel, vessel.obt_velocity, 0d, 0d);
                return;
            }
            double now = Planetarium.GetUniversalTime();
            double start = _guidance.BurnStartAt > 0d ? _guidance.BurnStartAt : _guidance.BurnUt - _guidance.BurnDuration * 0.5d;
            double end = _guidance.BurnEndAt > 0d ? _guidance.BurnEndAt : _guidance.BurnUt + _guidance.BurnDuration * 0.5d;
            double throttle = 0d;
            _guidance.LastAlignmentErrorDegrees = DirectionErrorDegrees(vessel, burnVector);
            if (now < start)
            {
                _guidance.Phase = "aligning_for_node_burn";
            }
            else if (now <= end)
            {
                // Do not spend a finite burn while the vehicle is still
                // pointing away from the requested vector. The window is
                // extended by the small amount of time spent aligning, so a
                // slow reaction wheel does not silently turn a correct node
                // into a large off-axis impulse.
                if (_guidance.LastAlignmentErrorDegrees > 8d)
                {
                    _guidance.Phase = "aligning_for_node_burn";
                    _guidance.BurnEndAt = Math.Min(_guidance.StartedAt + 3600d, end + Math.Min(2d, Math.Max(0.05d, Time.fixedDeltaTime)));
                }
                else
                {
                    bool engineReady = EnsureGuidanceEngineActive(vessel);
                    if (!engineReady)
                    {
                        _guidance.LastError = "no ignited/operational engine is available for the maneuver burn";
                        _guidance.Phase = "burn_engine_unavailable";
                    }
                    else
                    {
                        throttle = _guidance.BurnThrottle * ClampDouble(1d - _guidance.LastAlignmentErrorDegrees / 8d, 0.35d, 1d);
                        _guidance.Phase = "burning_node";
                    }
                }
            }
            else
            {
                _guidance.Phase = "burn_complete";
                if (!_guidance.BurnCompletionRecorded)
                {
                    _guidance.BurnCompletionRecorded = true;
                    KspMcpBridge bridge = KspMcpBridge.Instance;
                    if (bridge != null) bridge.RecordEvent("flight.maneuver_burn.completed", new Dictionary<string, object>
                    {
                        { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                        { "vessel_name", vessel == null ? null : vessel.vesselName },
                        { "node_index", _guidance.BurnNodeIndex },
                        { "ut", _guidance.BurnUt },
                        { "duration", _guidance.BurnDuration },
                        { "delta_v", _guidance.BurnDeltaV }
                    });
                }
            }
            ApplyDirectionControl(state, vessel, burnVector, throttle, 0d);
        }

        private static ManeuverNode FindManeuverNode(Vessel vessel, int index)
        {
            if (vessel == null || vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes == null) return null;
            if (index < 0 || index >= vessel.patchedConicSolver.maneuverNodes.Count) return null;
            return vessel.patchedConicSolver.maneuverNodes[index];
        }

        private static double AvailableGuidanceThrust(Vessel vessel)
        {
            if (vessel == null) return 0d;
            // KSP exposes currentStage as the next staging cursor. The live
            // engine is normally on that cursor, while a freshly launched
            // vessel can still report one cursor above it. Prefer the live
            // cursor first so transfer-stage burns do not accidentally use
            // the terminal/descent engine one stage lower.
            double currentStageThrust = SumStageThrust(vessel, vessel.currentStage);
            if (currentStageThrust > 0d) return currentStageThrust;
            double thrust = NumberMember(vessel, "availableThrust");
            if (thrust > 0d) return thrust;
            return EstimateActiveThrust(vessel);
        }

        private static double EstimateActiveThrust(Vessel vessel)
        {
            if (vessel == null || vessel.parts == null) return 0d;
            double currentStageThrust = SumStageThrust(vessel, vessel.currentStage);
            if (currentStageThrust > 0d) return currentStageThrust;
            int nextStage = Math.Max(0, vessel.currentStage - 1);
            double total = SumStageThrust(vessel, nextStage);
            if (total > 0d) return total;

            // Some editor-created vessels expose a stale currentStage value
            // immediately after launch. Fall back to the highest operational
            // engine stage instead of reporting that a perfectly good burn
            // has no engine.
            int fallbackStage = -1;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (BoolMember(module, "flameout")) continue;
                    if (!BoolMember(module, "isOperational") && !BoolMember(module, "moduleIsEnabled")) continue;
                    fallbackStage = Math.Max(fallbackStage, part.inverseStage);
                }
            }
            return fallbackStage < 0 ? 0d : SumStageThrust(vessel, fallbackStage);
        }

        private static double SumStageThrust(Vessel vessel, int stage)
        {
            double total = 0d;
            if (vessel == null || vessel.parts == null) return total;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.inverseStage != stage || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (BoolMember(module, "flameout")) continue;
                    if (!BoolMember(module, "isOperational") && !BoolMember(module, "moduleIsEnabled")) continue;
                    total += Math.Max(0d, NumberMember(module, "maxThrust"));
                }
            }
            return total;
        }

        private bool EnsureGuidanceEngineActive(Vessel vessel)
        {
            if (vessel == null || vessel.parts == null) return false;
            if (HasLiveEngine(vessel)) return true;

            int targetStage = Math.Max(0, vessel.currentStage - 1);
            if (!StageContainsEngine(vessel, targetStage)) targetStage = vessel.currentStage;
            if (!StageContainsEngine(vessel, targetStage)) return false;

            bool invoked = false;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.inverseStage != targetStage) continue;
                if (InvokeStagedPart(part, targetStage, vessel)) invoked = true;
            }
            if (!invoked) invoked = ActivateNextStageRaw();
            if (invoked && targetStage < vessel.currentStage) TrySetCurrentStage(vessel, targetStage);

            bool ready = HasLiveEngine(vessel) || HasOperationalEngine(vessel, targetStage);
            if (ready && _guidance != null && !_guidance.BurnIgnitionRecorded)
            {
                _guidance.BurnIgnitionRecorded = true;
                KspMcpBridge bridge = KspMcpBridge.Instance;
                if (bridge != null) bridge.RecordEvent("flight.ignition.guidance", new Dictionary<string, object>
                {
                    { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                    { "vessel_name", vessel == null ? null : vessel.vesselName },
                    { "stage", targetStage },
                    { "staging_cursor", vessel.currentStage }
                });
            }
            return ready;
        }

        private static bool StageContainsEngine(Vessel vessel, int stage)
        {
            if (vessel == null || vessel.parts == null) return false;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.inverseStage != stage || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module != null && module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        private static bool HasLiveEngine(Vessel vessel)
        {
            if (vessel == null || vessel.parts == null) return false;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (BoolMember(module, "engineIgnited") && !BoolMember(module, "flameout") &&
                        (BoolMember(module, "isOperational") || BoolMember(module, "moduleIsEnabled"))) return true;
                }
            }
            return false;
        }

        private static bool HasOperationalEngine(Vessel vessel, int stage)
        {
            if (vessel == null || vessel.parts == null) return false;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.inverseStage != stage || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!BoolMember(module, "flameout") && (BoolMember(module, "isOperational") || BoolMember(module, "engineIgnited"))) return true;
                }
            }
            return false;
        }

        public Dictionary<string, object> StartManeuverBurn(Dictionary<string, object> args)
        {
            var copy = new Dictionary<string, object>(args ?? new Dictionary<string, object>());
            copy["profile"] = "node_burn";
            return StartGuidance(copy);
        }

        public Dictionary<string, object> StartMoonSoftLanding(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (!JsonUtil.Boolean(args, "confirm", false))
            {
                throw new KspMcpException("confirmation_required", "moon soft landing requires confirm=true", null);
            }

            Vessel vessel = FlightGlobals.ActiveVessel;
            string requestedBody = JsonUtil.String(args, "target_body", "Mun");
            string activeBody = vessel == null || vessel.mainBody == null ? "" : vessel.mainBody.bodyName;
            string displayBody = vessel == null || vessel.mainBody == null ? "" : vessel.mainBody.theName;
            bool bodyMatches = string.Equals(activeBody, requestedBody, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(displayBody, requestedBody, StringComparison.OrdinalIgnoreCase);
            if (!bodyMatches)
            {
                throw new KspMcpException("moon_landing_body_mismatch", "the active vessel must already be at the requested moon; use the transfer planner and a capture burn first", new Dictionary<string, object>
                {
                    { "active_body", activeBody },
                    { "target_body", requestedBody }
                });
            }

            string situation = vessel.situation.ToString();
            if (string.Equals(situation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(situation, "LANDED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(situation, "SPLASHED", StringComparison.OrdinalIgnoreCase))
            {
                throw new KspMcpException("moon_landing_not_in_descent", "moon soft landing starts from a flying or orbital vessel, not a launch pad or an already landed vessel", situation);
            }

            var copy = new Dictionary<string, object>(args ?? new Dictionary<string, object>());
            copy["profile"] = "landing";
            copy["confirm"] = true;
            if (!copy.ContainsKey("target_altitude")) copy["target_altitude"] = 10d;
            Dictionary<string, object> result = StartGuidance(copy);
            result["mission"] = "moon_soft_landing";
            result["target_body"] = activeBody;
            result["landing_controller"] = "powered_descent_with_stopping_distance_and_surface_velocity_feedback";

            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge != null) bridge.RecordEvent("flight.moon_landing.started", new Dictionary<string, object>
            {
                { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                { "vessel_name", vessel == null ? null : vessel.vesselName },
                { "body", activeBody },
                { "target_latitude", JsonUtil.Has(args, "target_latitude") ? (object)JsonUtil.Number(args, "target_latitude", 0d) : null },
                { "target_longitude", JsonUtil.Has(args, "target_longitude") ? (object)JsonUtil.Number(args, "target_longitude", 0d) : null },
                { "auto_stage", JsonUtil.Boolean(args, "auto_stage", true) }
            });
            return result;
        }

        private void ApplyOrbitGuidance(FlightCtrlState state, Vessel vessel)
        {
            double altitude = Math.Max(0d, vessel.altitude);
            double apoapsis = vessel.orbit == null ? 0d : NumberMember(vessel.orbit, "ApA");
            double periapsis = vessel.orbit == null ? -1d : NumberMember(vessel.orbit, "PeA");

            // A vessel launched straight into the orbit profile should still
            // receive the same safe gravity turn until KSP reports a useful
            // bound orbit. Once Ap/Pe exist, this controller switches to
            // burns at the appropriate apsis instead of repeating ascent.
            if (vessel.orbit == null || altitude < 1000d || apoapsis < 1000d)
            {
                ApplyAscentGuidance(state, vessel);
                if (_guidance != null) _guidance.Phase = "orbital_ascent";
                return;
            }

            double targetApoapsis = _guidance.TargetApoapsis;
            double targetPeriapsis = _guidance.TargetPeriapsis;
            double apoapsisTolerance = Math.Max(250d, targetApoapsis * 0.01d);
            double periapsisTolerance = Math.Max(250d, Math.Max(1d, targetPeriapsis) * 0.01d);
            double timeToApoapsis = NumberMember(vessel.orbit, "timeToAp");
            double timeToPeriapsis = NumberMember(vessel.orbit, "timeToPe");
            Vector3d prograde = vessel.obt_velocity.sqrMagnitude > 0.01d
                ? vessel.obt_velocity.normalized
                : AscentTargetVector(vessel, 0d);
            Vector3d retrograde = -prograde;
            double throttle = 0d;
            Vector3d target = prograde;
            double targetPitch = 0d;

            if (apoapsis < targetApoapsis - apoapsisTolerance)
            {
                double error = targetApoapsis - apoapsis;
                throttle = ClampDouble(error / Math.Max(1000d, targetApoapsis * 0.25d), 0.15d, 1d);
                target = prograde;
                targetPitch = 0d;
                _guidance.Phase = "raise_apoapsis";
            }
            else if (periapsis < targetPeriapsis - periapsisTolerance)
            {
                if (timeToApoapsis > 15d)
                {
                    _guidance.Phase = "coast_to_circularisation";
                }
                else
                {
                    double error = targetPeriapsis - periapsis;
                    throttle = ClampDouble(error / Math.Max(1000d, targetPeriapsis * 0.2d), 0.08d, 1d);
                    target = prograde;
                    _guidance.Phase = "raise_periapsis_at_apoapsis";
                }
            }
            else if (periapsis > targetPeriapsis + periapsisTolerance)
            {
                if (timeToPeriapsis > 15d)
                {
                    target = retrograde;
                    _guidance.Phase = "coast_to_lowering_burn";
                }
                else
                {
                    double error = periapsis - targetPeriapsis;
                    throttle = ClampDouble(error / Math.Max(1000d, targetPeriapsis * 0.2d), 0.08d, 1d);
                    target = retrograde;
                    _guidance.Phase = "lower_periapsis_at_periapsis";
                }
            }
            else
            {
                _guidance.Phase = "orbit_achieved";
            }

            ApplyDirectionControl(state, vessel, target, throttle, targetPitch);
        }

        private void ApplyLandingGuidance(FlightCtrlState state, Vessel vessel)
        {
            // KSP's Vessel.terrainAltitude is the terrain elevation above the
            // body's sea-level datum, not the vessel's altitude above the
            // terrain. At 250 km over the Mun it is only a few kilometres,
            // which previously made an orbital vessel look like a low-altitude
            // descent vehicle and caused the controller to skip deorbit.
            double absoluteAltitude = Math.Max(0d, vessel.altitude);
            double terrainElevation = vessel.terrainAltitude;
            double altitude = Math.Max(0d, absoluteAltitude - terrainElevation);
            double verticalSpeed = vessel.verticalSpeed;
            double surfaceSpeed = vessel.srfSpeed;
            // Deorbit burns must use the vessel's inertial orbital velocity.
            // srf_velocity is relative to the rotating body surface; using it
            // for an orbital burn can add the body's rotation vector and turn
            // a nominal retrograde burn into a trajectory that raises the
            // apoapsis. Keep srf_velocity for surface-landing feedback below,
            // but use obt_velocity for the orbital retrograde direction.
            Vector3d orbitalVelocity = vessel.obt_velocity;
            Vector3d retrograde = orbitalVelocity.sqrMagnitude > 0.01d ? -orbitalVelocity.normalized : SurfaceNormal(vessel);

            // Landing from orbit needs a deorbit decision before the
            // powered-descent loop. The old controller entered descent while
            // still on a positive periapsis and therefore never produced a
            // trajectory that intersected the body.
            double apoapsis = vessel.orbit == null ? 0d : NumberMember(vessel.orbit, "ApA");
            double periapsis = vessel.orbit == null ? -1d : NumberMember(vessel.orbit, "PeA");
            double timeToApoapsis = vessel.orbit == null ? 0d : NumberMember(vessel.orbit, "timeToAp");
            string situation = vessel.situation.ToString();
            bool landed = string.Equals(situation, "LANDED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(situation, "SPLASHED", StringComparison.OrdinalIgnoreCase);

            if (_guidance.DeployGear && (landed || altitude <= _guidance.GearDeployAltitude))
            {
                state.gearDown = true;
                state.gearUp = false;
                if (!_guidance.GearCommanded)
                {
                    try { FireGroup("Gear", true); } catch (Exception exception) { _guidance.LastError = "gear deployment request failed: " + exception.Message; }
                    _guidance.GearCommanded = true;
                    KspMcpBridge bridge = KspMcpBridge.Instance;
                    if (bridge != null) bridge.RecordEvent("flight.landing.gear_commanded", new Dictionary<string, object>
                    {
                        { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                        { "vessel_name", vessel == null ? null : vessel.vesselName },
                        { "altitude", altitude },
                        { "absolute_altitude", absoluteAltitude },
                        { "terrain_altitude", vessel.terrainAltitude }
                    });
                }
            }
            bool deorbitNeedsBurn = !_guidance.DeorbitBurnCompleted &&
                (periapsis > -1000d || _guidance.DeorbitBurnStarted);
            // KSP may clamp a low Mun orbit to its exact safety boundary, so a
            // nominal 20 km orbit can arrive here as 19,999.98 m.  Requiring
            // apoapsis to be strictly greater than 20,000 m skips the
            // deorbit branch at that boundary and incorrectly labels the
            // vessel as ballistic while it is still in a stable orbit.  A
            // positive orbit is enough to enter the deorbit planner; the
            // periapsis and altitude guards above still prevent this from
            // taking over landed or non-orbital flight.
            if (!landed && absoluteAltitude > 10000d && deorbitNeedsBurn &&
                (apoapsis > 1000d || _guidance.DeorbitBurnStarted))
            {
                double throttle = 0d;
                double now = Planetarium.GetUniversalTime();
                bool ignitionHold = _guidance.IgnitionHoldUntil > now;
                bool burnAlreadyStarted = _guidance.DeorbitBurnStarted;
                if (burnAlreadyStarted && periapsis <= -1000d)
                {
                    _guidance.DeorbitBurnCompleted = true;
                    _guidance.Phase = "deorbit_burn_complete";
                    KspMcpBridge completedBridge = KspMcpBridge.Instance;
                    if (completedBridge != null) completedBridge.RecordEvent("flight.landing.deorbit_burn_completed", new Dictionary<string, object>
                    {
                        { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                        { "vessel_name", vessel == null ? null : vessel.vesselName },
                        { "periapsis", periapsis },
                        { "apoapsis", apoapsis }
                    });
                    TryDisableStockAutopilot(vessel);
                    ApplyDirectionControl(state, vessel, retrograde, 0d, 0d);
                    return;
                }
                // KSP resets timeToAp to the next orbit immediately after
                // crossing apoapsis.  A time-only test therefore misses the
                // burn window exactly at the event boundary.  The signed
                // vertical speed is the second half of the predicate: once
                // the vessel is descending, it has passed apoapsis even when
                // timeToAp has wrapped to the next orbit.
                bool pastApoapsis = timeToApoapsis <= 15d || verticalSpeed < -1d;
                if (!pastApoapsis && !burnAlreadyStarted && !ignitionHold)
                {
                    _guidance.Phase = "deorbit_coast_to_apoapsis";
                }
                else
                {
                    double error = periapsis + 1000d;
                    double alignmentError = DirectionErrorDegrees(vessel, retrograde);
                    bool attitudeReady = alignmentError <= 12d;
                    throttle = attitudeReady
                        ? ClampDouble(error / Math.Max(5000d, Math.Abs(periapsis) + 10000d), 0.1d, 1d)
                        : 0d;
                    _guidance.DeorbitBurnStarted = true;
                    if (attitudeReady)
                    {
                        if (ignitionHold) throttle = Math.Max(throttle, _guidance.IgnitionHoldThrottle);
                        _guidance.Phase = ignitionHold ? "automatic_ignition" : "deorbit_burn";
                    }
                    else
                    {
                        // Stage preparation may happen before stock SAS has
                        // finished turning a large vehicle.  Never trade
                        // orbital energy for attitude convergence: hold the
                        // engine at zero until the retrograde error is small.
                        _guidance.Phase = "deorbit_aligning";
                    }
                }
                ApplyDirectionControl(state, vessel, retrograde, throttle, 0d, "Retrograde");
                return;
            }

            // Keep a controlled descent speed at high altitude.  The old
            // -80 m/s gate let the controller oscillate between
            // coast_to_entry_burn and full-throttle braking: as soon as a
            // burn slowed the vessel above -80 m/s, the next frame stopped
            // the burn even though -80 m/s is still far too fast for a
            // powered landing.  Use the same conservative target that the
            // lower-altitude guidance loop uses instead.
            if (altitude > 30000d && verticalSpeed > -30d)
            {
                _guidance.Phase = "coast_to_entry_burn";
                TryDisableStockAutopilot(vessel);
                ApplyDirectionControl(state, vessel, retrograde, 0d, 0d);
                return;
            }

            double mass = Math.Max(0.001d, vessel.GetTotalMass());
            double thrust = AvailableGuidanceThrust(vessel);
            double maxAcceleration = thrust <= 0d ? 0d : thrust / mass;
            double gravity = SurfaceGravity(vessel, absoluteAltitude);
            double netBrakingAcceleration = Math.Max(0.1d, maxAcceleration - gravity);
            double downwardSpeed = Math.Max(0d, -verticalSpeed);
            double verticalStoppingDistance = downwardSpeed * downwardSpeed / (2d * netBrakingAcceleration);
            // An orbital landing vehicle is still carrying substantial
            // horizontal velocity after the deorbit burn.  Waiting only for
            // the vertical stopping distance starts the powered burn far too
            // late: on the Mun the vehicle can reach the ground while it is
            // still moving hundreds of metres per second sideways.  Include
            // the horizontal braking distance in the burn-window gate so the
            // same closed loop can cancel orbital velocity before touchdown.
            double horizontalStoppingDistance = surfaceSpeed * surfaceSpeed / (2d * netBrakingAcceleration);
            double stoppingDistance = Math.Max(verticalStoppingDistance, horizontalStoppingDistance);
            double desiredVerticalSpeed = altitude > 5000d ? -30d : (altitude > 500d ? -10d : -2d);
            // Allow the controller to command less than hover acceleration
            // when the vehicle is descending too slowly or climbing.  Clamping
            // this correction to zero made the powered loop settle at
            // gravity-compensation throttle high above the surface instead
            // of continuing toward the target descent speed.
            double verticalCorrection = (desiredVerticalSpeed - verticalSpeed) * 0.5d;
            // Surface speed contains both the radial and tangential velocity
            // components.  The powered braking phase may use the total speed
            // because Retrograde removes the complete velocity vector, but
            // the final touchdown controller must only ask for tangential
            // braking here.  Otherwise a vertical descent rate is incorrectly
            // converted into a horizontal braking request.
            Vector3d surfaceNormal = SurfaceNormal(vessel);
            Vector3d horizontalVelocity = ProjectOnPlane(vessel.srf_velocity, surfaceNormal);
            double horizontalSpeed = horizontalVelocity.magnitude;
            double horizontalBraking = horizontalSpeed * horizontalSpeed / Math.Max(20d, altitude * 2d);
            bool poweredDescentStarted =
                string.Equals(_guidance.Phase, "suicide_burn_and_retrograde_braking", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_guidance.Phase, "powered_descent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_guidance.Phase, "final_vertical_descent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_guidance.Phase, "final_hover_and_touchdown", StringComparison.OrdinalIgnoreCase);
            // Once the powered burn has started, keep the burn window open
            // until the touchdown controller takes over.  Recomputing only
            // from the now-decreasing stopping distance can otherwise turn
            // the engine off again immediately after a successful braking
            // impulse.
            bool burnWindow = poweredDescentStarted ||
                altitude <= stoppingDistance * 1.35d + 250d ||
                verticalSpeed < desiredVerticalSpeed ||
                altitude < 1000d;
            // Once translational speed is genuinely low, hand off to the
            // surface-normal touchdown controller and latch that decision.
            // A single speed threshold is not sufficient here: after the
            // handoff, the commanded descent speed can briefly make total
            // surface speed rise above the threshold again.  Without a latch
            // the controller would alternate between Retrograde braking and
            // vertical descent at high altitude, producing throttle and
            // attitude oscillation.  The latch is reset with every new plan
            // and remains active until touchdown or guidance stop.
            const double finalDescentEntrySpeed = 12d;
            if (burnWindow && !_guidance.FinalDescentLatched && surfaceSpeed <= finalDescentEntrySpeed)
            {
                _guidance.FinalDescentLatched = true;
                _guidance.FinalDescentEnteredAt = Planetarium.GetUniversalTime();
                KspMcpBridge latchBridge = KspMcpBridge.Instance;
                if (latchBridge != null) latchBridge.RecordEvent("flight.landing.final_descent_latched", new Dictionary<string, object>
                {
                    { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                    { "vessel_name", vessel == null ? null : vessel.vesselName },
                    { "surface_speed", surfaceSpeed },
                    { "vertical_speed", verticalSpeed },
                    { "altitude", absoluteAltitude },
                    { "height_agl", altitude },
                    { "entry_speed_limit_mps", finalDescentEntrySpeed }
                });
            }
            bool lowSpeedVerticalLanding = burnWindow && (_guidance.FinalDescentLatched || surfaceSpeed <= finalDescentEntrySpeed);
            double verticalDemand = Math.Max(0.1d, gravity + verticalCorrection);
            // Never let the final vector lose all upward authority just
            // because the vehicle is briefly climbing after a lateral
            // correction.  A small upward component keeps the lander above
            // the terrain while the horizontal component removes drift.
            if (lowSpeedVerticalLanding) verticalDemand = Math.Max(verticalDemand, gravity * 0.65d);
            double requiredAcceleration = verticalDemand;
            if (burnWindow && !lowSpeedVerticalLanding) requiredAcceleration += horizontalBraking;
            _guidance.LastStoppingDistance = stoppingDistance;
            _guidance.LastAvailableThrust = thrust;
            _guidance.LastTwr = gravity <= 0d ? 0d : maxAcceleration / gravity;

            Vector3d targetThrust = retrograde;
            double targetDistance;
            double targetHorizontalSpeed;
            double targetHorizontalAcceleration;
            if (TryLandingTargetThrust(vessel, altitude, verticalSpeed, gravity, out targetThrust, out targetDistance, out targetHorizontalSpeed, out targetHorizontalAcceleration))
            {
                _guidance.LastTargetDistance = targetDistance;
                _guidance.LastTargetHorizontalSpeed = targetHorizontalSpeed;
                if (burnWindow && !lowSpeedVerticalLanding)
                {
                    requiredAcceleration = Math.Sqrt(requiredAcceleration * requiredAcceleration + targetHorizontalAcceleration * targetHorizontalAcceleration);
                }
            }
            else
            {
                _guidance.LastTargetDistance = 0d;
                _guidance.LastTargetHorizontalSpeed = 0d;
            }
            // In the final phase the old controller added horizontal braking
            // to the throttle demand but still pointed the vehicle straight
            // along the surface normal.  That can never cancel tangential
            // velocity: it only makes the engine push harder vertically, and
            // a small attitude error then grows into a lateral runaway near
            // the ground.  Build one acceleration vector from the measured
            // local-up demand and the opposite horizontal velocity instead.
            // The vector is recomputed every frame, so this remains usable by
            // an AI without a camera or visual target.
            bool hasLandingTarget = _guidance.HasLandingTarget;
            if (lowSpeedVerticalLanding && !hasLandingTarget && horizontalSpeed > 0.25d)
            {
                double finalHorizontalBraking = Math.Min(horizontalBraking, Math.Max(0.1d, maxAcceleration * 0.85d));
                Vector3d touchdownAcceleration = surfaceNormal * verticalDemand - horizontalVelocity.normalized * finalHorizontalBraking;
                if (touchdownAcceleration.sqrMagnitude > 0.0001d)
                {
                    requiredAcceleration = touchdownAcceleration.magnitude;
                    targetThrust = touchdownAcceleration.normalized;
                }
            }
            // Ballistic descent must really be ballistic.  Computing the
            // gravity-compensation throttle outside the burn window made a
            // high-altitude lander hover at a small positive throttle after
            // deorbit, consuming propellant and never reaching the suicide
            // burn boundary.  Only ask for thrust when stopping-distance or
            // touchdown feedback says that braking is required.
            double throttleCommand = !burnWindow || maxAcceleration <= 0d
                ? 0d
                : ClampDouble(requiredAcceleration / maxAcceleration, 0d, 1d);

            if (landed || (altitude <= 5d && Math.Abs(verticalSpeed) < 2.5d && surfaceSpeed < 3d))
            {
                throttleCommand = 0d;
                _guidance.Phase = "landed_or_hovering";
                TryDisableStockAutopilot(vessel);
            }
            else if (maxAcceleration <= gravity)
            {
                throttleCommand = maxAcceleration <= 0d ? 0d : 1d;
                _guidance.LastError = "available thrust is not greater than local gravity; controlled landing is not possible";
                _guidance.Phase = "insufficient_landing_thrust";
            }
            else if (!burnWindow)
            {
                _guidance.Phase = "ballistic_descent";
                TryDisableStockAutopilot(vessel);
            }
            else if (altitude > 1000d) _guidance.Phase = lowSpeedVerticalLanding ? "final_vertical_descent" : "suicide_burn_and_retrograde_braking";
            else if (altitude > 200d) _guidance.Phase = "powered_descent";
            else _guidance.Phase = "final_hover_and_touchdown";
            // The powered-descent vector contains target-translation data,
            // but the first job of a suborbital lander is to remove its actual
            // surface velocity.  KSP's native Retrograde mode has already
            // been verified in this bridge's deorbit burn and tracks that
            // measured velocity directly.  Use it for the locked braking
            // window; retain the computed target vector for telemetry and
            // later low-speed touchdown feedback.
            // Keep the native Retrograde controller engaged while the lander
            // has no explicit landing target.  This is the stable no-vision
            // path: the stock controller points opposite the measured
            // velocity all the way through touchdown, while MCP still owns
            // the throttle, staging, gear, timeout, and safety decisions.
            // A coordinate landing target is different: it needs the vector
            // controller to add lateral translation, so only that path hands
            // attitude ownership from stock Retrograde to MCP.
            bool directFinalVector = lowSpeedVerticalLanding && hasLandingTarget;
            Vector3d attitudeTarget = burnWindow
                ? (directFinalVector ? targetThrust : retrograde)
                : targetThrust;
            string landingAutopilotMode = !burnWindow
                ? null
                : (directFinalVector ? null : "Retrograde");
            // The coordinate-target final vector is owned by MCP.  Disable
            // the previous stock Retrograde mode exactly once at that
            // hand-off; calling Disable every frame makes KSP fight the
            // direct PD loop, while never calling it leaves both writers
            // active during the last descent.  The no-target path deliberately
            // keeps Retrograde enabled because it is the more stable attitude
            // loop for a large vehicle with no visual landing target.
            if (directFinalVector && !string.IsNullOrEmpty(_stockAutopilotMode))
            {
                TryDisableStockAutopilot(vessel);
            }
            ApplyDirectionControl(state, vessel, attitudeTarget, throttleCommand, 0d, landingAutopilotMode);
        }

        private bool TryLandingTargetThrust(
            Vessel vessel,
            double altitude,
            double verticalSpeed,
            double gravity,
            out Vector3d thrustDirection,
            out double distance,
            out double desiredHorizontalSpeed,
            out double horizontalAcceleration)
        {
            thrustDirection = Vector3d.zero;
            distance = 0d;
            desiredHorizontalSpeed = 0d;
            horizontalAcceleration = 0d;
            if (_guidance == null || !_guidance.HasLandingTarget || vessel == null || vessel.mainBody == null) return false;

            Vector3d targetPosition;
            if (!TryGetWorldSurfacePosition(vessel.mainBody, _guidance.TargetLatitude, _guidance.TargetLongitude, 0d, out targetPosition)) return false;
            Vector3d up = SurfaceNormal(vessel);
            Vector3d displacement = ProjectOnPlane(targetPosition - vessel.GetWorldPos3D(), up);
            distance = displacement.magnitude;
            if (distance < 1d) return false;

            Vector3d targetDirection = displacement / distance;
            double timeHorizon = ClampDouble(altitude / Math.Max(5d, Math.Abs(verticalSpeed)), 5d, 30d);
            desiredHorizontalSpeed = ClampDouble(distance / timeHorizon, 0d, 80d);
            Vector3d currentHorizontalVelocity = ProjectOnPlane(vessel.srf_velocity, up);
            Vector3d desiredHorizontalVelocity = targetDirection * desiredHorizontalSpeed;
            Vector3d velocityError = desiredHorizontalVelocity - currentHorizontalVelocity;
            horizontalAcceleration = ClampDouble(velocityError.magnitude / 4d, 0d, Math.Max(2d, gravity * 1.5d));
            Vector3d horizontalCommand = velocityError.sqrMagnitude < 0.01d ? Vector3d.zero : velocityError.normalized * horizontalAcceleration;
            thrustDirection = up * Math.Max(0.5d, gravity) + horizontalCommand;
            if (thrustDirection.sqrMagnitude < 0.0001d) return false;
            thrustDirection.Normalize();
            return true;
        }

        private static Vector3d ProjectOnPlane(Vector3d vector, Vector3d normal)
        {
            if (normal.sqrMagnitude < 0.0001d) return vector;
            Vector3d unitNormal = normal.normalized;
            return vector - unitNormal * Vector3d.Dot(vector, unitNormal);
        }

        private static bool TryGetWorldSurfacePosition(CelestialBody body, double latitude, double longitude, double altitude, out Vector3d position)
        {
            position = Vector3d.zero;
            if (body == null) return false;
            try
            {
                MethodInfo method = body.GetType().GetMethod("GetWorldSurfacePosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(double), typeof(double), typeof(double) }, null);
                if (method == null) return false;
                object value = method.Invoke(body, new object[] { latitude, longitude, altitude });
                if (!(value is Vector3d)) return false;
                position = (Vector3d)value;
                return position.sqrMagnitude > 0.0001d;
            }
            catch (Exception) { return false; }
        }

        private void ApplyDirectionControl(FlightCtrlState state, Vessel vessel, Vector3d target, double throttle, double targetPitch, string stockAutopilotMode = null)
        {
            Transform controlTransform = ControlTransform(vessel);
            if (controlTransform == null) controlTransform = vessel == null ? null : vessel.transform;
            if (controlTransform == null) return;
            if (target.sqrMagnitude < 0.0001d) target = controlTransform.forward;
            target.Normalize();
            bool sasLocked = _sasEnabled && TryLockSasRotation(vessel, target);
            bool stockAutopilotActive = false;
            if (!string.IsNullOrEmpty(stockAutopilotMode))
            {
                stockAutopilotActive = TrySetStockAutopilotMode(vessel, stockAutopilotMode);
                sasLocked = stockAutopilotActive;
                if (stockAutopilotActive) RefreshStockAutopilot(vessel);
            }
            // KSP stack parts use their local +Y axis for the attachment and
            // longitudinal axis. ReferenceTransform is KSP's selected
            // control frame, but its Unity +Z forward axis is a transverse
            // frame axis for a normally oriented rocket. Treat +Y (the
            // reference transform's up axis) as the controllable nose axis;
            // this is the axis that points away from the engines and matches
            // the stock command-part orientation.
            Vector3 local = controlTransform.InverseTransformDirection((Vector3)target);
            Vector3 localAngularVelocity = controlTransform.InverseTransformDirection((Vector3)VectorMember(vessel, "angularVelocity"));
            double nose = Math.Max(0.1d, local.y);
            // ReferenceTransform is rotated relative to the intuitive
            // rocket frame: +Y is the controllable nose axis.  In the
            // FlightCtrlState mapping used by this bridge, the local +Z
            // error is corrected with the negative pitch input, while the
            // local +X error is corrected with positive yaw input.
            double pitchError = Math.Atan2(local.z, nose);
            double yawError = Math.Atan2(local.x, nose);
            // FlightCtrlState uses the opposite pitch sign from the local
            // +X rotation convention: a positive pitch command moves the
            // +Y rocket nose toward local -Z.  Positive yaw moves the nose
            // toward local +X.  Match the empirically verified KSP mapping
            // so the feedback closes toward the target instead of diverging.
            // Large editor-built stacks have slow, elastic attitude response.
            // A high proportional gain with very little damping makes the
            // vehicle overshoot the local-normal target, then saturate a
            // gimbal in the opposite direction.  Use a deliberately damped
            // PD controller so a no-visual launch remains recoverable while
            // a human can still take over through the ordinary control lease.
            float pitch = Clamp((float)(-pitchError * 1.2d - localAngularVelocity.x * 0.55d), -1f, 1f);
            float yaw = Clamp((float)(yawError * 1.2d - localAngularVelocity.z * 0.55d), -1f, 1f);
            state.mainThrottle = Clamp((float)throttle, 0f, 1f);
            // Stock VesselAutopilot writes its PID result through
            // Vessel.OnAutopilotUpdate before this bridge's OnFlyByWire
            // callback.  Do not replace that result with zeroed controls:
            // doing so leaves the telemetry mode saying Retrograde/RadialOut
            // while the engines continue firing in the old attitude.  Keep
            // the stock callback's pitch/yaw/roll/killRot values intact and
            // only own throttle.  If stock autopilot is unavailable, retain
            // the direct MCP PD fallback.
            if (!stockAutopilotActive)
            {
                // FlightCtrlState.killRot is the actual per-frame SAS switch;
                // changing only Vessel.ActionGroups is not sufficient on all KSP
                // revisions, especially while another fly-by-wire callback owns
                // the guidance loop.
                state.killRot = sasLocked;
                state.pitch = sasLocked ? 0f : pitch;
                state.yaw = sasLocked ? 0f : yaw;
                state.roll = sasLocked ? 0f : Clamp(-localAngularVelocity.y * 0.45f, -1f, 1f);
            }
            ApplyEngineThrottle(vessel, state.mainThrottle);
            _guidance.LastThrottle = state.mainThrottle;
            _guidance.LastPitch = state.pitch;
            _guidance.LastYaw = state.yaw;
            _guidance.LastTargetPitchDegrees = targetPitch;
            _guidance.LastAlignmentErrorDegrees = DirectionErrorDegrees(vessel, target);
            _guidance.LastAvailableThrust = AvailableGuidanceThrust(vessel);
            double gravity = SurfaceGravity(vessel, Math.Max(0d, vessel.altitude));
            double mass = Math.Max(0.001d, vessel.GetTotalMass());
            _guidance.LastTwr = gravity <= 0d ? 0d : _guidance.LastAvailableThrust / (mass * gravity);
        }

        private static bool CanEngageStockAutopilot(Vessel vessel)
        {
            if (vessel == null || vessel.Autopilot == null) return false;
            try
            {
                object sas = vessel.Autopilot.SAS;
                // Some stock/modded revisions do not expose an SAS object;
                // in that case the mode API itself is the only available
                // capability probe.  When SAS is present, however, a false
                // CanEngageSAS must be treated as a hard refusal so the MCP
                // PD fallback remains in control.
                return sas == null || InvokeBoolMethod(sas, "CanEngageSAS");
            }
            catch (Exception) { return false; }
        }

        private bool TrySetStockAutopilotMode(Vessel vessel, string modeName)
        {
            if (vessel == null || vessel.Autopilot == null || string.IsNullOrEmpty(modeName)) return false;
            if (!CanEngageStockAutopilot(vessel))
            {
                _stockAutopilotMode = null;
                return false;
            }
            // Remembering the last requested mode is not proof that KSP is
            // still running it.  Probe-controlled vessels and vessels without
            // an engageable SAS can leave the autopilot disabled while the
            // requested mode string remains cached.  If we returned true in
            // that state, ApplyDirectionControl would suppress the MCP PD
            // fallback and the vehicle would drift with zero attitude input.
            if (string.Equals(_stockAutopilotMode, modeName, StringComparison.OrdinalIgnoreCase))
            {
                bool enabled = BoolMember(vessel.Autopilot, "Enabled");
                string actual = TextMember(vessel.Autopilot, "Mode");
                if (enabled && string.Equals(actual, modeName, StringComparison.OrdinalIgnoreCase))
                {
                    RefreshStockAutopilot(vessel);
                    return true;
                }
                _stockAutopilotMode = null;
            }
            try
            {
                bool sasActionGroupApplied = SetVesselActionGroupState(vessel, "SAS", true);
                // KSP's action-group collection is not sufficient to turn on
                // live SAS for every revision. Explicitly fire the group;
                // this is what makes VesselAutopilot.OnAutopilotUpdate run.
                try { FireGroup("SAS", true); }
                catch (Exception exception)
                {
                    if (!sasActionGroupApplied) KspMcpBridge.Log("could not fire SAS action group: " + exception.Message);
                }
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                object autopilot = vessel.Autopilot;
                foreach (MethodInfo method in autopilot.GetType().GetMethods(flags))
                {
                    if (!string.Equals(method.Name, "Enable", StringComparison.OrdinalIgnoreCase)) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum) continue;
                    object enumValue = Enum.Parse(parameters[0].ParameterType, modeName, true);
                    object result = method.Invoke(autopilot, new object[] { enumValue });
                    if (result is bool && !(bool)result) continue;
                    RefreshStockAutopilot(vessel);
                    bool enabled = BoolMember(autopilot, "Enabled");
                    string actual = TextMember(autopilot, "Mode");
                    if (!enabled || !CanEngageStockAutopilot(vessel) || !string.Equals(actual, modeName, StringComparison.OrdinalIgnoreCase))
                    {
                        _stockAutopilotMode = null;
                        return false;
                    }
                    _stockAutopilotMode = modeName;
                    return true;
                }
                foreach (MethodInfo method in autopilot.GetType().GetMethods(flags))
                {
                    if (!string.Equals(method.Name, "SetMode", StringComparison.OrdinalIgnoreCase)) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum) continue;
                    object enumValue = Enum.Parse(parameters[0].ParameterType, modeName, true);
                    object result = method.Invoke(autopilot, new object[] { enumValue });
                    if (result is bool && !(bool)result) continue;
                    RefreshStockAutopilot(vessel);
                    bool enabled = BoolMember(autopilot, "Enabled");
                    string actual = TextMember(autopilot, "Mode");
                    if (!enabled || !CanEngageStockAutopilot(vessel) || !string.Equals(actual, modeName, StringComparison.OrdinalIgnoreCase))
                    {
                        _stockAutopilotMode = null;
                        return false;
                    }
                    _stockAutopilotMode = modeName;
                    return true;
                }
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("stock autopilot mode failed: " + modeName + ": " + exception.Message);
            }
            return false;
        }

        private void TryDisableStockAutopilot(Vessel vessel)
        {
            if (vessel == null || vessel.Autopilot == null || string.IsNullOrEmpty(_stockAutopilotMode))
            {
                _stockAutopilotMode = null;
                return;
            }
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                MethodInfo disable = vessel.Autopilot.GetType().GetMethod("Disable", flags, null, Type.EmptyTypes, null);
                if (disable != null) disable.Invoke(vessel.Autopilot, null);
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("stock autopilot disable failed: " + exception.Message);
            }
            try { FireGroup("SAS", false); } catch (Exception) { }
            _stockAutopilotMode = null;
        }

        private static void RefreshStockAutopilot(Vessel vessel)
        {
            if (vessel == null || vessel.Autopilot == null) return;
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                if (!BoolMember(vessel.Autopilot, "Enabled"))
                {
                    MethodInfo enable = vessel.Autopilot.GetType().GetMethod("Enable", flags, null, Type.EmptyTypes, null);
                    if (enable != null) enable.Invoke(vessel.Autopilot, null);
                }
                MethodInfo update = vessel.Autopilot.GetType().GetMethod("Update", flags, null, Type.EmptyTypes, null);
                if (update != null) update.Invoke(vessel.Autopilot, null);
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("stock autopilot refresh failed: " + exception.Message);
            }
        }

        private static void ApplyEngineThrottle(Vessel vessel, float throttle)
        {
            if (vessel == null || vessel.parts == null) return;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool staged = BoolMember(module, "staged");
                    bool ignited = BoolMember(module, "engineIgnited");
                    if (!staged && !ignited && throttle <= 0.001f) continue;
                    SetMember(module, "requestedThrottle", Clamp(throttle, 0f, 1f));
                    try
                    {
                        MethodInfo updateThrottle = module.GetType().GetMethod("UpdateThrottle", flags, null, Type.EmptyTypes, null);
                        if (updateThrottle != null) updateThrottle.Invoke(module, null);
                    }
                    catch (Exception exception)
                    {
                        KspMcpBridge.Log("direct engine throttle update failed: " + exception.GetType().Name + ": " + exception.Message);
                    }
                }
            }
        }

        private static double DirectionErrorDegrees(Vessel vessel, Vector3d target)
        {
            if (vessel == null || target.sqrMagnitude < 0.0001d) return 180d;
            Transform controlTransform = ControlTransform(vessel);
            if (controlTransform == null) return 180d;
            target.Normalize();
            Vector3 local = controlTransform.InverseTransformDirection((Vector3)target);
            double nose = Math.Max(0.0001d, local.y);
            double lateral = Math.Sqrt(local.x * local.x + local.z * local.z);
            return Math.Abs(Math.Atan2(lateral, nose) * 180d / Math.PI);
        }

        private static Transform ControlTransform(Vessel vessel)
        {
            if (vessel == null) return null;
            try
            {
                Transform reference = vessel.ReferenceTransform;
                if (reference != null) return reference;
            }
            catch (Exception) { }
            return vessel.transform;
        }

        private static bool TryLockSasRotation(Vessel vessel, Vector3d target)
        {
            if (vessel == null || vessel.Autopilot == null || vessel.Autopilot.SAS == null) return false;
            try
            {
                // A SAS object can exist while the current command source is
                // not allowed to engage it (for example an uncrewed probe
                // without the required connection/control state).  Treating
                // the LockRotation call as success in that case suppresses
                // the direct MCP PD fallback and leaves the vehicle with no
                // attitude input at all.  Use the same capability probe as
                // the stock autopilot path before claiming the lock.
                if (!InvokeBoolMethod(vessel.Autopilot.SAS, "CanEngageSAS")) return false;
                Vector3 forward = (Vector3)target;
                if (forward.sqrMagnitude < 0.0001f) return false;
                forward.Normalize();
                Transform reference = ControlTransform(vessel);
                if (reference == null) return false;
                // The live reference transform already contains the correct
                // KSP roll convention (and, on the launch pad, its +Y axis
                // is exactly the surface normal).  Rotate that current axis
                // directly toward the target and preserve its roll instead
                // of reconstructing a quaternion with an assumed +/-90°
                // reference-frame offset.
                Quaternion desired = Quaternion.FromToRotation(reference.up, forward) * reference.rotation;
                vessel.Autopilot.SAS.LockRotation(desired);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static Dictionary<string, object> ControlFrameSnapshot(Vessel vessel)
        {
            Transform physical = vessel == null ? null : vessel.transform;
            Transform control = ControlTransform(vessel);
            if (physical == null || control == null) return null;
            Vector3d surfaceNormal = SurfaceNormal(vessel);
            Vector3d surfaceEasting = SurfaceEasting(vessel);
            Vector3d surfacePrograde = SurfacePrograde(vessel);
            Vector3d launchTarget = surfaceNormal;
            Vector3 localLaunchTarget = control.InverseTransformDirection((Vector3)launchTarget);
            return new Dictionary<string, object>
            {
                { "source", control == physical ? "vessel_transform" : "reference_transform" },
                { "forward", JsonUtil.Vector3Object(control.forward) },
                { "up", JsonUtil.Vector3Object(control.up) },
                { "right", JsonUtil.Vector3Object(control.right) },
                { "vessel_forward", JsonUtil.Vector3Object(physical.forward) },
                { "vessel_up", JsonUtil.Vector3Object(physical.up) },
                { "vessel_right", JsonUtil.Vector3Object(physical.right) },
                { "surface_normal", JsonUtil.Vector3Object(surfaceNormal) },
                { "surface_easting", JsonUtil.Vector3Object(surfaceEasting) },
                { "surface_prograde", JsonUtil.Vector3Object(surfacePrograde) },
                { "local_surface_normal", JsonUtil.Vector3Object(localLaunchTarget) },
                { "longitudinal_axis", "reference_up_y" },
                { "ascent_direction", "surface_prograde" }
            };
        }

        private void TryAutomaticStage(Vessel vessel)
        {
            if (_guidance == null || vessel == null || vessel.currentStage <= 0) return;
            double now = Planetarium.GetUniversalTime();
            _lastGuidanceStageAt = now;

            // A custom stage action changes the cursor immediately, while
            // engine ignition and decoupler physics settle over subsequent
            // FixedUpdate ticks.  Give that newly active row a short grace
            // period so a requested burn cannot cascade through the next
            // separator before its engine has had a chance to light.
            if (vessel.currentStage == _lastAutomaticStageActivatedCursor &&
                now - _lastAutomaticStageActivatedAt < 2d) return;

            // KSP exposes currentStage as the staging cursor. The action
            // that Space/StageManager will trigger next is one lower, and
            // parts carry that lower number in inverseStage. The important
            // distinction is that currentStage is also the row that was just
            // activated and is still burning. Looking only at the next row
            // makes the already-enabled future engines look like permission
            // to advance every 0.25 seconds.
            int stagingCursorBefore = vessel.currentStage;
            int nextStage = Math.Max(0, stagingCursorBefore - 1);
            // A staging cursor can bounce back to the previous row after a
            // separator fires. A successful target row is a one-shot action
            // for this guidance session; do not re-ignite it if KSP exposes
            // the old cursor again several seconds later.
            if (_automaticStageTargetsActivated.Contains(nextStage)) return;
            Dictionary<string, object> nextStageSummary = StageActionSummary(vessel, nextStage);
            double timeToApoapsis = vessel.orbit == null ? double.PositiveInfinity : NumberMember(vessel.orbit, "timeToAp");
            double apoapsis = vessel.orbit == null ? 0d : NumberMember(vessel.orbit, "ApA");
            double targetApoapsis = _guidance.Profile == "ascent" ? _guidance.TargetApoapsis : 0d;
            double apoapsisTolerance = targetApoapsis > 0d ? Math.Max(500d, targetApoapsis * 0.02d) : double.PositiveInfinity;
            bool apoapsisSeparationWindow = _guidance.Profile == "ascent" &&
                _guidance.AscentLaunchStageCursor != int.MinValue &&
                stagingCursorBefore == _guidance.AscentLaunchStageCursor &&
                string.Equals(_guidance.Phase, "coast_to_apoapsis", StringComparison.OrdinalIgnoreCase) &&
                // Separate before the upper-stage burn needs to start. A
                // 45-second lead is enough for the tested large stack and
                // still leaves the launch stage safely above its target ApA.
                timeToApoapsis <= 45d &&
                apoapsis >= targetApoapsis - apoapsisTolerance;
            bool activeStageEnginePending = false;
            bool nextStageHasEngine = false;
            bool nextStageEngineReady = false;
            bool nextStageIgnited = false;
            bool nextStageHasAction = false;
            bool hasLowerAction = false;
            bool anyIgnitedEngine = false;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null) continue;
                    string moduleName = module.GetType().Name;
                    bool isEngine = moduleName.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isDecoupler = moduleName.IndexOf("Decouple", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        moduleName.IndexOf("Separator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        moduleName.IndexOf("Seperator", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isEngine && !isDecoupler) continue;
                    if (part.inverseStage == nextStage) nextStageHasAction = true;
                    else if (part.inverseStage >= 0 && part.inverseStage < nextStage) hasLowerAction = true;
                    if (!isEngine) continue;
                    bool flameout = BoolMember(module, "flameout");
                    bool operational = BoolMember(module, "isOperational");
                    bool ignited = BoolMember(module, "engineIgnited");
                    bool enabled = BoolMember(module, "moduleIsEnabled") || BoolMember(module, "isEnabled");
                    double finalThrust = Math.Max(0d, NumberMember(module, "finalThrust"));
                    double requestedThrottle = NumberMember(module, "requestedThrottle");
                    double currentThrottle = NumberMember(module, "currentThrottle");
                    if (ignited) anyIgnitedEngine = true;
                    if (part.inverseStage == stagingCursorBefore)
                    {
                        // isOperational remains true for a staged engine even
                        // when the throttle is zero during an apoapsis coast.
                        // It is not evidence that the current row is still
                        // producing thrust.  Gate the hold on actual thrust,
                        // or on a not-yet-ignited engine that is receiving a
                        // burn request.  This lets an exhausted/coasting row
                        // advance while still protecting the ignition window.
                        bool producingThrust = finalThrust > 0.5d;
                        bool ignitionPending = !ignited && operational &&
                            (requestedThrottle > 0.05d || currentThrottle > 0.05d);
                        if (!flameout && (producingThrust || ignitionPending))
                        {
                            activeStageEnginePending = true;
                        }
                    }
                    if (part.inverseStage == nextStage)
                    {
                        nextStageHasEngine = true;
                        if (ignited) nextStageIgnited = true;
                        if (!flameout && (operational || ignited || enabled)) nextStageEngineReady = true;
                    }
                }
            }

            // Do not gate ignition on speed. A correctly staged vessel can
            // still be at (or very near) zero surface speed immediately after
            // launch. More importantly, never inspect only nextStage while
            // the current cursor still owns a non-flameout engine. This is
            // the invariant that prevents premature staging and separation.
            if (activeStageEnginePending) return;

            // The next row is the one that should be ignited now. Stage()
            // invokes the actual part actions and synchronously moves KSP's
            // staging cursor, which is more reliable for editor-created
            // ModuleEnginesFX vessels than repeatedly calling StageManager.
            if (nextStageHasEngine && nextStageEngineReady && !nextStageIgnited &&
                (_guidance.LastThrottle > 0.05d || apoapsisSeparationWindow))
            {
                // KSP can briefly expose the previous staging cursor again
                // while a decoupler and its child vessel settle. If the same
                // lower row was just activated, do not issue a second Stage()
                // call during that cursor bounce.
                if (_lastAutomaticStageActivatedCursor == nextStage &&
                    now - _lastAutomaticStageActivatedAt < 4d) return;
                // Avoid hammering a row if a modded action did not move the
                // cursor. Retry after a short backoff, but never emit a false
                // ignition event for an unchanged cursor.
                if (stagingCursorBefore == _lastAutomaticStageCommandCursor && now - _lastAutomaticStageCommandAt < 2.0d) return;
                _lastAutomaticStageCommandCursor = stagingCursorBefore;
                _lastAutomaticStageCommandAt = now;
                bool activated = false;
                try
                {
                    Stage();
                    activated = vessel.currentStage != stagingCursorBefore;
                }
                catch (Exception exception) { _guidance.LastError = exception.Message; }
                if (!activated)
                {
                    // Keep a reflection fallback for stock/modded setups
                    // where the custom action path is unavailable.
                    activated = ActivateNextStageRaw() && vessel.currentStage != stagingCursorBefore;
                }
                if (activated)
                {
                    _automaticStageTargetsActivated.Add(nextStage);
                    _lastAutomaticStageActivatedCursor = vessel.currentStage;
                    _lastAutomaticStageActivatedAt = now;
                    if (_guidance != null)
                    {
                        _guidance.Phase = "automatic_ignition";
                        _guidance.IgnitionAttempts++;
                        // KSP may apply a custom engine activation one frame
                        // before the new staging row sees FlightCtrlState.
                        // Keep a positive throttle request alive for a short,
                        // observable window so a no-visual client can wait
                        // for engineIgnited instead of guessing from timing.
                        _guidance.IgnitionHoldThrottle = Math.Max(0.35d, _guidance.LastThrottle);
                        _guidance.IgnitionHoldUntil = now + 3d;
                        KspMcpBridge ignitionBridge = KspMcpBridge.Instance;
                        if (ignitionBridge != null) ignitionBridge.RecordEvent("flight.ignition.hold_started", new Dictionary<string, object>
                        {
                            { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                            { "vessel_name", vessel == null ? null : vessel.vesselName },
                            { "stage", nextStage },
                            { "throttle", _guidance.IgnitionHoldThrottle },
                            { "duration_seconds", 3d },
                            { "attempt", _guidance.IgnitionAttempts }
                        });
                    }
                    KspMcpBridge bridge = KspMcpBridge.Instance;
                    if (bridge != null) bridge.RecordEvent("flight.ignition.automatic", new Dictionary<string, object>
                    {
                        { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                        { "vessel_name", vessel == null ? null : vessel.vesselName },
                        { "stage", nextStage },
                        { "staging_cursor_before", stagingCursorBefore },
                        { "staging_cursor_after", vessel.currentStage },
                        { "reason", "ignition" },
                        { "engine_count", nextStageSummary["engine_count"] },
                        { "decoupler_count", nextStageSummary["decoupler_count"] },
                        { "separation", nextStageSummary["decoupler_count"] is int && (int)nextStageSummary["decoupler_count"] > 0 }
                    });
                }
                return;
            }

            // Empty staging rows are legal in KSP. Advance through one when
            // a lower actionable stage exists instead of leaving guidance
            // permanently parked on an empty cursor.
            if (!nextStageHasAction && !hasLowerAction) return;
            // Do not advance an untouched prelaunch/upper-stage cursor while
            // the guidance controller is intentionally coasting at zero
            // throttle. A stage following a real flameout remains eligible
            // because anyIgnitedEngine is still true in that case.
            if (!anyIgnitedEngine && _guidance.LastThrottle <= 0.05d) return;
            if (stagingCursorBefore == _lastAutomaticStageCommandCursor && now - _lastAutomaticStageCommandAt < 2.0d) return;
            _lastAutomaticStageCommandCursor = stagingCursorBefore;
            _lastAutomaticStageCommandAt = now;
            try
            {
                Stage();
                if (vessel.currentStage == stagingCursorBefore)
                {
                    _guidance.LastError = "automatic stage action did not advance the staging cursor";
                    return;
                }
                _automaticStageTargetsActivated.Add(nextStage);
                _lastAutomaticStageActivatedCursor = vessel.currentStage;
                _lastAutomaticStageActivatedAt = now;
                if (_guidance != null) _guidance.Phase = "automatic_stage";
                KspMcpBridge bridge = KspMcpBridge.Instance;
                if (bridge != null) bridge.RecordEvent("flight.stage.automatic", new Dictionary<string, object>
                {
                    { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                    { "vessel_name", vessel == null ? null : vessel.vesselName },
                    { "stage", nextStage },
                    { "staging_cursor_before", stagingCursorBefore },
                    { "staging_cursor_after", vessel.currentStage },
                    { "reason", anyIgnitedEngine ? "engine_flameout_or_empty_stage" : "empty_action_stage" },
                    { "engine_count", nextStageSummary["engine_count"] },
                    { "decoupler_count", nextStageSummary["decoupler_count"] },
                    { "separation", nextStageSummary["decoupler_count"] is int && (int)nextStageSummary["decoupler_count"] > 0 }
                });
            }
            catch (Exception exception)
            {
                if (_guidance != null) _guidance.LastError = exception.Message;
            }
        }

        private static Dictionary<string, object> StageActionSummary(Vessel vessel, int stage)
        {
            int engines = 0;
            int decouplers = 0;
            if (vessel != null && vessel.parts != null)
            {
                foreach (Part part in vessel.parts)
                {
                    if (part == null || part.inverseStage != stage || part.Modules == null) continue;
                    foreach (PartModule module in part.Modules)
                    {
                        if (module == null) continue;
                        string name = module.GetType().Name;
                        if (name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) >= 0) engines++;
                        if (name.IndexOf("Decouple", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Separator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Seperator", StringComparison.OrdinalIgnoreCase) >= 0) decouplers++;
                    }
                }
            }
            return new Dictionary<string, object>
            {
                { "stage", stage },
                { "engine_count", engines },
                { "decoupler_count", decouplers }
            };
        }

        private void PrepareLaunchStageForGuidance(string profile, Vessel vessel)
        {
            if (vessel == null || (profile != "ascent" && profile != "orbit")) return;
            string situation = vessel.situation.ToString();
            if (!string.Equals(situation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase)) return;
            int before = vessel.currentStage;
            if (before <= 0) return;
            int target = before - 1;
            Dictionary<string, object> summary = StageActionSummary(vessel, target);
            int engineCount = (int)summary["engine_count"];
            int decouplerCount = (int)summary["decoupler_count"];
            if (engineCount <= 0)
            {
                if (decouplerCount > 0)
                {
                    throw new KspMcpException("guidance_initial_stage_unsafe", "the first staging row contains separation hardware but no engine; refusing to separate on the launch pad", summary);
                }
                throw new KspMcpException("guidance_initial_stage_missing", "the first staging row has no usable engine action", summary);
            }

            try
            {
                Stage();
            }
            catch (Exception exception)
            {
                throw new KspMcpException("guidance_initial_stage_failed", "could not activate the launch engine staging row: " + exception.Message, summary);
            }
            if (vessel.currentStage == before)
            {
                throw new KspMcpException("guidance_initial_stage_failed", "KSP did not advance the launch staging cursor", new Dictionary<string, object>
                {
                    { "stage_before", before },
                    { "stage_target", target },
                    { "engine_count", engineCount },
                    { "decoupler_count", decouplerCount }
                });
            }

            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge != null) bridge.RecordEvent("flight.ignition.launch_prepared", new Dictionary<string, object>
            {
                { "vessel_id", vessel.id.ToString() },
                { "vessel_name", vessel.vesselName },
                { "stage_before", before },
                { "stage_activated", target },
                { "stage_after", vessel.currentStage },
                { "engine_count", engineCount },
                { "decoupler_count", decouplerCount },
                { "reason", "guidance_start_on_prelaunch_vessel" }
            });
        }

        private static double EstimateCircularisationBurnDuration(Vessel vessel, double targetApoapsis, double targetPeriapsis, out double targetDeltaV)
        {
            targetDeltaV = 0d;
            if (vessel == null || vessel.mainBody == null || vessel.mainBody.gravParameter <= 0d) return 30d;
            double bodyRadius = Math.Max(1d, vessel.mainBody.Radius);
            double currentRadius = Math.Max(bodyRadius + 1d, bodyRadius + Math.Max(0d, vessel.altitude));
            double apoapsisRadius = Math.Max(currentRadius, bodyRadius + Math.Max(0d, targetApoapsis));
            double periapsisRadius = Math.Max(bodyRadius + 1d, bodyRadius + Math.Max(0d, targetPeriapsis));
            double orbitRadiusSum = Math.Max(2d, apoapsisRadius + periapsisRadius);
            double targetSpeedSquared = vessel.mainBody.gravParameter *
                (2d / currentRadius - 2d / orbitRadiusSum);
            double targetSpeed = targetSpeedSquared > 0d ? Math.Sqrt(targetSpeedSquared) : 0d;
            double currentSpeed = vessel.obt_velocity.sqrMagnitude > 0.01d ? vessel.obt_velocity.magnitude : 0d;
            targetDeltaV = Math.Max(0d, targetSpeed - currentSpeed);
            double thrust = AvailableGuidanceThrust(vessel);
            double mass = Math.Max(0.001d, vessel.GetTotalMass());
            double acceleration = thrust / mass;
            if (targetDeltaV <= 1d || acceleration <= 0.1d) return targetDeltaV <= 1d ? 0d : 30d;

            // Acceleration increases as propellant is consumed. The factor is
            // intentionally conservative so the impulse ends before the
            // transfer stage can drive the vehicle onto an escape trajectory.
            double effectiveAcceleration = Math.Max(0.1d, acceleration * 1.25d);
            double duration = targetDeltaV / effectiveAcceleration * 1.15d;
            return ClampDouble(duration, 5d, 120d);
        }

        private static double EstimateCircularisationResidualDeltaV(Vessel vessel, double targetApoapsis, double targetPeriapsis, out double targetDeltaV)
        {
            targetDeltaV = 0d;
            if (vessel == null || vessel.mainBody == null || vessel.mainBody.gravParameter <= 0d) return 0d;
            double bodyRadius = Math.Max(1d, vessel.mainBody.Radius);
            double currentRadius = Math.Max(bodyRadius + 1d, bodyRadius + Math.Max(0d, vessel.altitude));
            double apoapsisRadius = Math.Max(currentRadius, bodyRadius + Math.Max(0d, targetApoapsis));
            double periapsisRadius = Math.Max(bodyRadius + 1d, bodyRadius + Math.Max(0d, targetPeriapsis));
            double radiusSum = Math.Max(2d, apoapsisRadius + periapsisRadius);
            double targetSpeedSquared = vessel.mainBody.gravParameter *
                (2d / currentRadius - 2d / radiusSum);
            double targetSpeed = targetSpeedSquared > 0d ? Math.Sqrt(targetSpeedSquared) : 0d;
            double currentSpeed = vessel.obt_velocity.sqrMagnitude > 0.01d ? vessel.obt_velocity.magnitude : 0d;
            targetDeltaV = Math.Max(0d, targetSpeed - currentSpeed);
            return targetDeltaV;
        }

        private static double EstimateFiniteBurnDuration(Vessel vessel, double deltaV)
        {
            if (vessel == null || deltaV <= 0d) return 0d;
            double thrust = AvailableGuidanceThrust(vessel);
            double mass = Math.Max(0.001d, vessel.GetTotalMass());
            double acceleration = thrust / mass;
            if (acceleration <= 0.1d) return 0d;
            return ClampDouble(deltaV / acceleration * 1.10d, 0.25d, 15d);
        }

        private static bool IsNearApoapsisForTrim(Vessel vessel, double targetApoapsis, double tolerance)
        {
            if (vessel == null) return false;
            double altitude = Math.Max(0d, vessel.altitude);
            double verticalSpeed = Math.Abs(vessel.verticalSpeed);
            double altitudeWindow = Math.Max(2000d, tolerance * 3d);
            return altitude >= targetApoapsis - altitudeWindow && verticalSpeed <= 35d;
        }

        private static int DetectAscentLaunchStageCursor(Vessel vessel)
        {
            if (vessel == null) return int.MinValue;
            string situation = vessel.situation.ToString();
            if (!string.Equals(situation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase)) return int.MinValue;
            return Math.Max(0, vessel.currentStage - 1);
        }

        private static int DetectAscentTransferStageCursor(Vessel vessel, int launchStageCursor)
        {
            if (vessel == null || launchStageCursor == int.MinValue || launchStageCursor <= 0) return int.MinValue;
            for (int stage = launchStageCursor - 1; stage >= 0; stage--)
            {
                if (StageContainsEngine(vessel, stage)) return stage;
            }
            return int.MinValue;
        }

        private bool IsAscentTransferStageActive(Vessel vessel)
        {
            if (_guidance == null || !string.Equals(_guidance.Profile, "ascent", StringComparison.OrdinalIgnoreCase)) return true;
            if (vessel == null || _guidance.AscentTransferStageCursor == int.MinValue) return false;
            if (vessel.currentStage == _guidance.AscentTransferStageCursor) return true;
            // A separator may make KSP expose the old cursor again even
            // though the lower engine has already ignited.  The automatic
            // stage controller records successful target rows as one-shot
            // activations; reuse that authoritative fact for burn guidance.
            return _automaticStageTargetsActivated.Contains(_guidance.AscentTransferStageCursor);
        }

        private static bool ActivateNextStageRaw()
        {
            MethodInfo method = typeof(KSP.UI.Screens.StageManager).GetMethod("ActivateNextStage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) return false;
            try
            {
                method.Invoke(null, null);
                return true;
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("automatic ignition failed: " + exception.Message);
                return false;
            }
        }

        private static Vector3d AscentTargetVector(Vessel vessel, double pitchDegrees)
        {
            Vector3d up = SurfaceNormal(vessel);
            // In KSP's body frame the vector returned by GetSurfaceEasting is
            // the opposite of the inertial prograde direction used by the
            // stock orbit solver.  Launching along it produces a 180-degree
            // retrograde orbit once the rocket's velocity dominates Kerbin's
            // surface rotation.  Keep the raw vector in telemetry, but use
            // its verified prograde counterpart for ascent guidance.
            Vector3d east = SurfacePrograde(vessel);
            double radians = pitchDegrees * Math.PI / 180d;
            Vector3d result = up * Math.Sin(radians) + east * Math.Cos(radians);
            if (result.sqrMagnitude < 0.0001d) return up;
            result.Normalize();
            return result;
        }

        private static double SurfaceGravity(Vessel vessel, double altitude)
        {
            if (vessel == null || vessel.mainBody == null) return 9.80665d;
            double radius = Math.Max(1d, vessel.mainBody.Radius + Math.Max(0d, altitude));
            double mu = vessel.mainBody.gravParameter;
            if (mu <= 0d) return 9.80665d;
            return mu / (radius * radius);
        }

        private static double HeightAboveTerrain(Vessel vessel)
        {
            if (vessel == null) return 0d;
            // Vessel.altitude is measured from the body's sea-level datum;
            // terrainAltitude is the terrain elevation at the vessel's
            // latitude/longitude. A visionless controller needs this explicit
            // AGL value for gear, suicide-burn, and touchdown decisions.
            return Math.Max(0d, Math.Max(0d, vessel.altitude) - vessel.terrainAltitude);
        }

        private static Vector3d SurfaceNormal(Vessel vessel)
        {
            Vector3d fallback = vessel.GetWorldPos3D();
            if (vessel.mainBody != null) fallback -= (Vector3d)vessel.mainBody.transform.position;
            if (fallback.sqrMagnitude < 0.0001d) fallback = new Vector3d(0d, 1d, 0d);
            fallback.Normalize();
            return InvokeBodyVector(vessel, "GetSurfaceNVector", fallback);
        }

        private static Vector3d SurfaceEasting(Vessel vessel)
        {
            Vector3d fallback = Vector3d.Cross(new Vector3d(0d, 1d, 0d), SurfaceNormal(vessel));
            if (fallback.sqrMagnitude < 0.0001d) fallback = new Vector3d(1d, 0d, 0d);
            fallback.Normalize();
            return InvokeBodyVector(vessel, "GetSurfaceEasting", fallback);
        }

        private static Vector3d SurfacePrograde(Vessel vessel)
        {
            Vector3d easting = SurfaceEasting(vessel);
            if (easting.sqrMagnitude < 0.0001d) return easting;
            easting.Normalize();
            return -easting;
        }

        private static Vector3d InvokeBodyVector(Vessel vessel, string methodName, Vector3d fallback)
        {
            if (vessel == null || vessel.mainBody == null) return fallback;
            try
            {
                MethodInfo method = vessel.mainBody.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method != null)
                {
                    object value = method.Invoke(vessel.mainBody, new object[] { vessel.latitude, vessel.longitude });
                    if (value is Vector3d)
                    {
                        Vector3d result = (Vector3d)value;
                        if (result.sqrMagnitude > 0.0001d) { result.Normalize(); return result; }
                    }
                }
            }
            catch (Exception) { }
            return fallback;
        }

        public Dictionary<string, object> SetSas(Dictionary<string, object> args)
        {
            EnsureFlight();
            _sasEnabled = JsonUtil.Boolean(args, "enabled", false);
            Vessel vessel = FlightGlobals.ActiveVessel;
            bool actionGroupApplied = SetVesselActionGroupState(vessel, "SAS", _sasEnabled);
            // Some KSP revisions accept SetGroup but do not update the live
            // FlightCtrlState. Fire the action in both cases so telemetry and
            // the actual SAS controller agree.
            try { FireGroup("SAS", _sasEnabled); }
            catch (Exception)
            {
                if (!actionGroupApplied) throw;
            }
            return new Dictionary<string, object>
            {
                { "sas", _sasEnabled },
                { "stock_action_group_applied", actionGroupApplied },
                { "state", Snapshot() }
            };
        }

        public Dictionary<string, object> SetRcs(Dictionary<string, object> args)
        {
            EnsureFlight();
            _rcsEnabled = JsonUtil.Boolean(args, "enabled", false);
            Vessel vessel = FlightGlobals.ActiveVessel;
            bool actionGroupApplied = SetVesselActionGroupState(vessel, "RCS", _rcsEnabled);
            if (!actionGroupApplied) FireGroup("RCS", _rcsEnabled);
            return new Dictionary<string, object>
            {
                { "rcs", _rcsEnabled },
                { "stock_action_group_applied", actionGroupApplied },
                { "state", Snapshot() }
            };
        }

        public Dictionary<string, object> Stage()
        {
            EnsureFlight();
            Vessel vessel = FlightGlobals.ActiveVessel;
            int before = vessel.currentStage;
            if (before <= 0) throw new KspMcpException("no_next_stage", "the vessel is already at its final staging index", before);
            int target = Math.Max(0, before - 1);
            List<Part> stagedParts = new List<Part>();
            foreach (Part part in vessel.parts)
            {
                if (part != null && part.inverseStage == target) stagedParts.Add(part);
            }
            // Match KSP's normal staging order. The in-stage index is the
            // order used by the stock staging stack for parts sharing one
            // stage, which matters when a separator and an engine are fired
            // together.
            stagedParts.Sort(delegate(Part left, Part right)
            {
                return right.inStageIndex.CompareTo(left.inStageIndex);
            });
            int stagedEngineCount = 0;
            int stagedDecouplerCount = 0;
            foreach (Part stagedPart in stagedParts)
            {
                if (stagedPart == null || stagedPart.Modules == null) continue;
                foreach (PartModule module in stagedPart.Modules)
                {
                    if (module == null) continue;
                    string moduleName = module.GetType().Name;
                    if (moduleName.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) >= 0) stagedEngineCount++;
                    if (moduleName.IndexOf("Decouple", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        moduleName.IndexOf("Separator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        moduleName.IndexOf("Seperator", StringComparison.OrdinalIgnoreCase) >= 0) stagedDecouplerCount++;
                }
            }

            // StageManager is reliable for stock decouplers, but it does not
            // consistently invoke ModuleEnginesFX actions on vessels created
            // through the editor API. Invoke the actual KSPAction first, then
            // move the current-stage cursor ourselves. This preserves the
            // normal highest-to-lowest staging order while making engine
            // ignition deterministic for MCP-built vessels.
            bool invoked = false;
            foreach (Part part in stagedParts)
            {
                if (InvokeStagedPart(part, target, vessel)) invoked = true;
            }

            if (invoked || stagedParts.Count == 0)
            {
                TrySetCurrentStage(vessel, target);
                KspMcpBridge bridge = KspMcpBridge.Instance;
                if (bridge != null) bridge.RecordEvent("flight.stage.activated", new Dictionary<string, object>
                {
                    { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                    { "vessel_name", vessel == null ? null : vessel.vesselName },
                    { "stage_before", before },
                    { "stage_activated", target },
                    { "custom_activation", invoked },
                    { "engine_count", stagedEngineCount },
                    { "decoupler_count", stagedDecouplerCount },
                    { "separation", stagedDecouplerCount > 0 }
                });
                return new Dictionary<string, object>
                {
                    { "staged", true },
                    { "stage_before", before },
                    { "stage_activated", target },
                    { "stage_after", vessel.currentStage },
                    { "custom_activation", invoked },
                    { "engine_count", stagedEngineCount },
                    { "decoupler_count", stagedDecouplerCount },
                    { "separation", stagedDecouplerCount > 0 },
                    { "state", Snapshot() }
                };
            }

            MethodInfo method = typeof(KSP.UI.Screens.StageManager).GetMethod("ActivateNextStage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) throw new KspMcpException("stage_unavailable", "KSP StageManager.ActivateNextStage is not available", null);
            method.Invoke(null, null);
            KspMcpBridge bridgeAfter = KspMcpBridge.Instance;
            if (bridgeAfter != null) bridgeAfter.RecordEvent("flight.stage.activated", new Dictionary<string, object>
            {
                { "vessel_id", vessel == null ? null : vessel.id.ToString() },
                { "vessel_name", vessel == null ? null : vessel.vesselName },
                { "stage_before", before },
                { "stage_activated", target },
                { "custom_activation", false },
                { "engine_count", stagedEngineCount },
                { "decoupler_count", stagedDecouplerCount },
                { "separation", stagedDecouplerCount > 0 }
            });
            return new Dictionary<string, object>
            {
                { "staged", true },
                { "stage_before", before },
                { "stage_activated", target },
                { "stage_after", vessel.currentStage },
                { "custom_activation", false },
                { "engine_count", stagedEngineCount },
                { "decoupler_count", stagedDecouplerCount },
                { "separation", stagedDecouplerCount > 0 },
                { "state", Snapshot() }
            };
        }

        public Dictionary<string, object> Warp(Dictionary<string, object> args)
        {
            int index = JsonUtil.Integer(args, "rate_index", 0);
            if (index < 0) index = 0;
            // KSP physics/time-warp levels can advance orbital state without
            // running a reliable OnFlyByWire control frame for every step.
            // A no-visual controller must stay at real time while guidance is
            // active so ignition, staging, burns, and touchdown cannot be
            // skipped between callbacks.
            if (_guidance != null && index > 0)
            {
                throw new KspMcpException("warp_unsafe_during_guidance", "guidance plans allow only real time; stop guidance before using time warp so a no-visual controller cannot skip ignition, staging, or touchdown events", new Dictionary<string, object>
                {
                    { "requested_rate_index", index },
                    { "safe_max_rate_index", 0 },
                    { "profile", _guidance.Profile },
                    { "phase", _guidance.Phase }
                });
            }
            MethodInfo[] methods = typeof(TimeWarp).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (MethodInfo method in methods)
            {
                if (method.Name != "SetRate") continue;
                ParameterInfo[] parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 3 &&
                        parameters[0].ParameterType == typeof(int) &&
                        parameters[1].ParameterType == typeof(bool) &&
                        parameters[2].ParameterType == typeof(bool))
                    {
                        method.Invoke(null, new object[] { index, true, true });
                        return new Dictionary<string, object> { { "rate_index", index }, { "set", true } };
                    }
                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(bool))
                    {
                        method.Invoke(null, new object[] { index, true });
                        return new Dictionary<string, object> { { "rate_index", index }, { "set", true } };
                    }
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                    {
                        method.Invoke(null, new object[] { index });
                        return new Dictionary<string, object> { { "rate_index", index }, { "set", true } };
                    }
                }
                catch (Exception exception)
                {
                    throw new KspMcpException("warp_failed", exception.Message, null);
                }
            }
            throw new KspMcpException("warp_unavailable", "KSP TimeWarp.SetRate is not available", null);
        }

        public Dictionary<string, object> Bodies(Dictionary<string, object> args)
        {
            string query = JsonUtil.String(args, "query", "").ToLowerInvariant();
            var result = new List<object>();
            if (FlightGlobals.Bodies != null)
            {
                foreach (CelestialBody body in FlightGlobals.Bodies)
                {
                    if (body == null) continue;
                    string name = body.bodyName ?? "";
                    if (query.Length > 0 && name.ToLowerInvariant().IndexOf(query, StringComparison.Ordinal) < 0) continue;
                    result.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "display_name", body.theName },
                        { "radius_m", body.Radius },
                        { "grav_parameter_m3_s2", body.gravParameter },
                        { "sphere_of_influence_m", body.sphereOfInfluence },
                        { "atmosphere", body.atmosphere },
                        { "max_atmosphere_altitude_m", NumberMember(body, "atmosphereDepth") },
                        { "ocean", body.ocean },
                        { "reference_body", body.referenceBody == null ? null : body.referenceBody.bodyName },
                        { "orbit", body.orbit == null ? null : OrbitSummary(body.orbit) }
                    });
                }
            }
            return new Dictionary<string, object> { { "count", result.Count }, { "bodies", result } };
        }

        public Dictionary<string, object> ManeuverNodes()
        {
            EnsureFlight();
            Vessel vessel = FlightGlobals.ActiveVessel;
            var nodes = new List<object>();
            if (vessel.patchedConicSolver != null && vessel.patchedConicSolver.maneuverNodes != null)
            {
                foreach (ManeuverNode node in vessel.patchedConicSolver.maneuverNodes)
                {
                    if (node == null) continue;
                    Vector3d deltaV = node.DeltaV;
                    Vector3d burnVector = vessel.orbit == null ? deltaV : node.GetBurnVector(vessel.orbit);
                    nodes.Add(new Dictionary<string, object>
                    {
                        { "ut", node.UT },
                        { "eta", node.UT - Planetarium.GetUniversalTime() },
                        { "radial_plus_mps", deltaV.x },
                        { "normal_plus_mps", -deltaV.y },
                        { "prograde_mps", deltaV.z },
                        { "delta_v_mps", Math.Sqrt(deltaV.sqrMagnitude) },
                        { "delta_v_node_coordinates", JsonUtil.Vector3dObject(deltaV) },
                        { "burn_vector_world", JsonUtil.Vector3dObject(burnVector) },
                        { "orbit_after", node.nextPatch == null ? null : OrbitSummary(node.nextPatch) }
                    });
                }
            }
            return new Dictionary<string, object> { { "count", nodes.Count }, { "nodes", nodes } };
        }

        public Dictionary<string, object> AddManeuverNode(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (!JsonUtil.Boolean(args, "confirm", false)) throw new KspMcpException("confirmation_required", "adding a maneuver node requires confirm=true", null);
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel.patchedConicSolver == null) throw new KspMcpException("maneuver_unavailable", "active vessel has no patched conic solver", null);
            double defaultUt = Planetarium.GetUniversalTime() + (vessel.orbit == null ? 60d : Math.Max(1d, vessel.orbit.timeToAp));
            double ut = Math.Max(Planetarium.GetUniversalTime() + 0.1d, JsonUtil.Number(args, "ut", defaultUt));
            double radial = JsonUtil.Number(args, "radial", 0d);
            double normalPlus = JsonUtil.Number(args, "normal", 0d);
            double prograde = JsonUtil.Number(args, "prograde", 0d);
            ManeuverNode node = vessel.patchedConicSolver.AddManeuverNode(ut);
            if (node == null) throw new KspMcpException("maneuver_failed", "KSP did not return a maneuver node", null);
            node.OnGizmoUpdated(new Vector3d(radial, -normalPlus, prograde), ut);
            KspMcpBridge bridge = KspMcpBridge.Instance;
            if (bridge != null) bridge.RecordEvent("flight.maneuver_node.added", new Dictionary<string, object> { { "ut", ut }, { "prograde", prograde }, { "normal", normalPlus }, { "radial", radial } });
            return new Dictionary<string, object> { { "added", true }, { "node", ManeuverNodes() } };
        }

        public Dictionary<string, object> ClearManeuverNodes(Dictionary<string, object> args)
        {
            EnsureFlight();
            if (!JsonUtil.Boolean(args, "confirm", false)) throw new KspMcpException("confirmation_required", "clearing maneuver nodes requires confirm=true", null);
            Vessel vessel = FlightGlobals.ActiveVessel;
            int count = 0;
            if (vessel.patchedConicSolver != null && vessel.patchedConicSolver.maneuverNodes != null)
            {
                var nodes = new List<ManeuverNode>(vessel.patchedConicSolver.maneuverNodes);
                foreach (ManeuverNode node in nodes)
                {
                    if (node == null) continue;
                    try { vessel.patchedConicSolver.RemoveManeuverNode(node); } catch (Exception) { try { node.RemoveSelf(); } catch (Exception) { } }
                    count++;
                }
            }
            return new Dictionary<string, object> { { "cleared", count }, { "nodes", ManeuverNodes() } };
        }

        public Dictionary<string, object> ActivatePart(Dictionary<string, object> args)
        {
            EnsureFlight();
            Part part = null;
            string id = JsonUtil.String(args, "part_id", null);
            if (!string.IsNullOrEmpty(id)) part = _craft.FindPartForControl(id);
            if (part == null && JsonUtil.Has(args, "flight_id"))
            {
                uint flightId = (uint)Math.Max(0, JsonUtil.Integer(args, "flight_id", 0));
                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel != null) part = vessel[flightId];
            }
            if (part == null) throw new KspMcpException("part_not_found", "flight part not found", null);
            string eventName = JsonUtil.String(args, "event", null);
            if (string.IsNullOrEmpty(eventName))
            {
                bool activated = false;
                try { activated = part.activate(part.inverseStage, FlightGlobals.ActiveVessel); }
                catch (Exception exception) { KspMcpBridge.Log("part.activate failed: " + exception.Message); }
                bool engineInvoked = InvokeEngineModules(part);
                bool actionInvoked = false;
                if (!engineInvoked) actionInvoked = InvokePartAction(part, "ActivateAction");
                if (!activated && !engineInvoked && !actionInvoked) part.force_activate();
            }
            else if (eventName.EndsWith("Action", StringComparison.OrdinalIgnoreCase))
            {
                if (!InvokePartAction(part, eventName)) part.SendEvent(eventName);
            }
            else part.SendEvent(eventName);
            return new Dictionary<string, object> { { "activated", true }, { "part_id", id }, { "event", eventName } };
        }

        private static bool InvokeStagedPart(Part part, int stage, Vessel vessel)
        {
            if (part == null) return false;
            bool partActivated = false;
            try
            {
                partActivated = part.activate(stage, vessel);
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("part.activate stage failed: " + exception.Message);
            }
            if (part.Modules == null) return false;
            foreach (PartModule module in part.Modules)
            {
                if (module == null) continue;
                string moduleName = module.GetType().Name;
                if (moduleName.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return InvokeEngineModule(module) || InvokePartAction(part, "ActivateAction") || partActivated;
                }
                if (moduleName.IndexOf("ModuleDecouple", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    moduleName.IndexOf("ModuleAnchoredDecoupler", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    part.SendEvent("Decouple");
                    return true;
                }
            }
            return partActivated;
        }

        private static bool InvokeEngineModules(Part part)
        {
            if (part == null || part.Modules == null) return false;
            foreach (PartModule module in part.Modules)
            {
                if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (InvokeEngineModule(module)) return true;
            }
            return false;
        }

        private static bool InvokeEngineModule(PartModule module)
        {
            if (module == null) return false;
            try
            {
                ModuleEngines engine = module as ModuleEngines;
                if (engine != null)
                {
                    engine.Activate();
                }
                else
                {
                    // ModuleEnginesFX and modded engine modules can expose
                    // the same activation method without inheriting the
                    // stock ModuleEngines type used by this KSP build.
                    MethodInfo activate = module.GetType().GetMethod("Activate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (activate == null) return false;
                    activate.Invoke(module, null);
                }
                KspMcpBridge.Log("direct engine Activate invoked module=" + module.GetType().Name);
                return true;
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("direct engine Activate failed: " + exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        private static bool InvokePartAction(Part part, string actionName)
        {
            if (part == null || part.Modules == null || string.IsNullOrEmpty(actionName)) return false;
            foreach (PartModule module in part.Modules)
            {
                if (module == null || module.Actions == null) continue;
                BaseAction action = module.Actions[actionName];
                if (action == null) continue;
                action.Invoke(new KSPActionParam(KSPActionGroup.None, KSPActionType.Activate));
                InvokeEngineModule(module);
                return true;
            }
            return false;
        }

        private static bool TrySetCurrentStage(Vessel vessel, int stage)
        {
            if (vessel == null) return false;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                FieldInfo field = vessel.GetType().GetField("currentStage", flags);
                if (field != null && field.FieldType == typeof(int))
                {
                    field.SetValue(vessel, stage);
                    return true;
                }
                PropertyInfo property = vessel.GetType().GetProperty("currentStage", flags);
                MethodInfo setter = property == null ? null : property.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(vessel, new object[] { stage });
                    return true;
                }
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("could not set current stage: " + exception.Message);
            }
            return false;
        }

        public Dictionary<string, object> Abort()
        {
            EnsureFlight();
            FireGroup("Abort", true);
            return new Dictionary<string, object> { { "abort_fired", true }, { "state", Snapshot() } };
        }

        public Dictionary<string, object> ClearControl()
        {
            _lease.Clear();
            _leaseUntil = 0d;
            return new Dictionary<string, object> { { "control_released", true }, { "controls", ControlSnapshot() } };
        }

        public Dictionary<string, object> ReturnToEditor(Dictionary<string, object> args)
        {
            EnsureFlight();
            string mode = JsonUtil.String(args, "editor_mode", "VAB").ToUpperInvariant();
            EditorFacility facility;
            if (mode == "VAB") facility = EditorFacility.VAB;
            else if (mode == "SPH") facility = EditorFacility.SPH;
            else throw new KspMcpException("invalid_editor_mode", "editor_mode must be VAB or SPH", mode);

            // A revert/recovery can leave the active flight vessel alive for
            // a few frames. Release MCP control before KSP restores the editor
            // so no stale throttle or steering lease crosses the scene change.
            _guidance = null;
            _lease.Clear();
            _leaseUntil = 0d;
            try
            {
                FlightDriver.ReturnToEditor(facility);
                KspMcpBridge bridge = KspMcpBridge.Instance;
                if (bridge != null) bridge.RecordEvent("flight.return_to_editor.requested", new Dictionary<string, object>
                {
                    { "editor_mode", mode },
                    { "entry", "mcp" }
                });
                return new Dictionary<string, object>
                {
                    { "return_requested", true },
                    { "editor_mode", mode },
                    { "method", "ReturnToEditor" }
                };
            }
            catch (Exception exception)
            {
                throw new KspMcpException("return_to_editor_failed", "KSP could not return to the editor: " + exception.Message, null);
            }
        }

        public Dictionary<string, object> Recover()
        {
            _guidance = null;
            _lease.Clear();
            _leaseUntil = 0d;
            MethodInfo[] methods = typeof(FlightDriver).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (string name in new[] { "RevertToPrelaunch", "RevertToLaunch", "RecoverVessel" })
            {
                foreach (MethodInfo method in methods)
                {
                    if (method.Name != name || method.GetParameters().Length != 0) continue;
                    method.Invoke(null, null);
                    return new Dictionary<string, object> { { "recover_requested", true }, { "method", name } };
                }
            }
            throw new KspMcpException("recover_unavailable", "KSP does not expose a supported recovery method in this version", null);
        }

        public Dictionary<string, object> Snapshot()
        {
            Vessel vessel = null;
            try { vessel = FlightGlobals.ActiveVessel; } catch (Exception) { }
            if (vessel == null)
            {
                return new Dictionary<string, object> { { "available", false }, { "control_lease", ControlSnapshot() } };
            }

            var result = new Dictionary<string, object>
            {
                { "available", true },
                { "vessel_name", vessel.vesselName },
                { "vessel_id", vessel.id.ToString() },
                { "active", vessel.isActiveVessel },
                { "loaded", vessel.loaded },
                { "universal_time", Planetarium.GetUniversalTime() },
                { "commandable", vessel.isCommandable },
                { "situation", vessel.situation.ToString() },
                { "body", vessel.mainBody == null ? null : vessel.mainBody.bodyName },
                { "mission_time", vessel.missionTime },
                { "altitude", vessel.altitude },
                { "terrain_altitude", vessel.terrainAltitude },
                { "height_agl", HeightAboveTerrain(vessel) },
                { "altitude_semantics", "sea_level_datum" },
                { "height_agl_semantics", "vessel_to_local_terrain" },
                { "surface_speed", vessel.srfSpeed },
                { "orbital_speed", vessel.obt_speed },
                { "vertical_speed", vessel.verticalSpeed },
                { "latitude", vessel.latitude },
                { "longitude", vessel.longitude },
                { "mass_tonnes", vessel.GetTotalMass() },
                { "current_stage", vessel.currentStage },
                { "next_stage", Math.Max(0, vessel.currentStage - 1) },
                { "position", JsonUtil.Vector3dObject(vessel.GetWorldPos3D()) },
                { "velocity", JsonUtil.Vector3dObject(vessel.obt_velocity) },
                { "orientation", JsonUtil.QuaternionObject(vessel.transform.rotation) },
                { "control_frame", ControlFrameSnapshot(vessel) },
                { "controls", CurrentControlState(vessel.ctrlState) },
                { "control_lease", ControlSnapshot() },
                { "guidance", GuidanceStatus() },
                { "resources", AggregateResources(vessel) },
                { "engines", EngineSnapshot(vessel) }
            };

            if (vessel.orbit != null)
            {
                result["orbit"] = new Dictionary<string, object>
                {
                    { "apoapsis", NumberMember(vessel.orbit, "ApA") },
                    { "periapsis", NumberMember(vessel.orbit, "PeA") },
                    { "eccentricity", NumberMember(vessel.orbit, "eccentricity") },
                    { "inclination", NumberMember(vessel.orbit, "inclination") },
                    { "period", NumberMember(vessel.orbit, "period") },
                    { "time_to_apoapsis", NumberMember(vessel.orbit, "timeToAp") },
                    { "time_to_periapsis", NumberMember(vessel.orbit, "timeToPe") }
                };
            }
            return result;
        }

        /// <summary>
        /// A deliberately small telemetry record for high-rate polling.
        /// Snapshot() is intentionally detailed for inspection, but it walks
        /// every resource and engine module. Sending that payload every frame
        /// made a visionless MCP client both slow and unnecessarily noisy.
        /// </summary>
        public Dictionary<string, object> CompactSnapshot()
        {
            Vessel vessel = null;
            try { vessel = FlightGlobals.ActiveVessel; } catch (Exception) { }
            if (vessel == null)
            {
                return new Dictionary<string, object>
                {
                    { "available", false },
                    { "control_lease", ControlSnapshot() }
                };
            }

            var result = new Dictionary<string, object>
            {
                { "available", true },
                { "vessel_name", vessel.vesselName },
                { "vessel_id", vessel.id.ToString() },
                { "active", vessel.isActiveVessel },
                { "loaded", vessel.loaded },
                { "commandable", vessel.isCommandable },
                { "situation", vessel.situation.ToString() },
                { "body", vessel.mainBody == null ? null : vessel.mainBody.bodyName },
                { "universal_time", Planetarium.GetUniversalTime() },
                { "mission_time", vessel.missionTime },
                { "altitude", vessel.altitude },
                { "terrain_altitude", vessel.terrainAltitude },
                { "height_agl", HeightAboveTerrain(vessel) },
                { "altitude_semantics", "sea_level_datum" },
                { "height_agl_semantics", "vessel_to_local_terrain" },
                { "surface_speed", vessel.srfSpeed },
                { "orbital_speed", vessel.obt_speed },
                { "vertical_speed", vessel.verticalSpeed },
                { "latitude", vessel.latitude },
                { "longitude", vessel.longitude },
                { "mass_tonnes", vessel.GetTotalMass() },
                { "current_stage", vessel.currentStage },
                { "next_stage", Math.Max(0, vessel.currentStage - 1) },
                { "position", JsonUtil.Vector3dObject(vessel.GetWorldPos3D()) },
                { "velocity", JsonUtil.Vector3dObject(vessel.obt_velocity) },
                { "orientation", JsonUtil.QuaternionObject(vessel.transform.rotation) },
                { "control_frame", ControlFrameSnapshot(vessel) },
                { "controls", CurrentControlState(vessel.ctrlState) },
                { "control_lease", ControlSnapshot() },
                { "guidance", GuidanceStatus() },
                { "engine_summary", CompactEngineSummary(vessel) },
                { "resources", CompactResourceSummary(vessel) }
            };

            if (vessel.orbit != null)
            {
                result["orbit"] = new Dictionary<string, object>
                {
                    { "apoapsis", NumberMember(vessel.orbit, "ApA") },
                    { "periapsis", NumberMember(vessel.orbit, "PeA") },
                    { "eccentricity", NumberMember(vessel.orbit, "eccentricity") },
                    { "inclination", NumberMember(vessel.orbit, "inclination") },
                    { "period", NumberMember(vessel.orbit, "period") },
                    { "time_to_apoapsis", NumberMember(vessel.orbit, "timeToAp") },
                    { "time_to_periapsis", NumberMember(vessel.orbit, "timeToPe") }
                };
            }
            return result;
        }

        private Dictionary<string, object> CompactEngineSummary(Vessel vessel)
        {
            RefreshCompactSummary(vessel);
            return _compactEngineSummary ?? EngineSummary(vessel);
        }

        private List<object> CompactResourceSummary(Vessel vessel)
        {
            RefreshCompactSummary(vessel);
            return _compactResources ?? AggregateResources(vessel);
        }

        private void RefreshCompactSummary(Vessel vessel)
        {
            if (vessel == null) return;
            float now = Time.realtimeSinceStartup;
            string vesselId = vessel.id.ToString();
            if (_compactEngineSummary != null &&
                string.Equals(_compactSummaryVesselId, vesselId, StringComparison.Ordinal) &&
                _lastCompactSummaryAt >= 0f && now - _lastCompactSummaryAt < 0.25f) return;
            _compactSummaryVesselId = vesselId;
            _lastCompactSummaryAt = now;
            _compactEngineSummary = EngineSummary(vessel);
            _compactResources = AggregateResources(vessel);
        }

        private void EnsureFlight()
        {
            if (FlightGlobals.ActiveVessel == null) throw new KspMcpException("no_active_vessel", "KSP has no active vessel", KspMcpBridge.SceneName());
        }

        private void FireGroup(string groupName, bool activate)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            KSPActionGroup group;
            if (!TryParseGroup(groupName, out group)) throw new KspMcpException("invalid_action_group", "unknown action group: " + groupName, null);
            MethodInfo[] methods = typeof(BaseAction).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            KSPActionType actionType = activate ? KSPActionType.Activate : KSPActionType.Deactivate;
            foreach (MethodInfo method in methods)
            {
                if (method.Name != "FireAction") continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 4)
                {
                    method.Invoke(null, new object[] { vessel.parts, group, -1, actionType });
                    return;
                }
                if (parameters.Length == 3)
                {
                    method.Invoke(null, new object[] { vessel.parts, group, actionType });
                    return;
                }
            }
            throw new KspMcpException("action_unavailable", "KSP BaseAction.FireAction is not available", null);
        }

        private static bool SetVesselActionGroupState(Vessel vessel, string groupName, bool enabled)
        {
            if (vessel == null) return false;
            KSPActionGroup group;
            if (!TryParseGroup(groupName, out group)) return false;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                object actionGroups = null;
                PropertyInfo property = vessel.GetType().GetProperty("ActionGroups", flags);
                if (property != null) actionGroups = property.GetValue(vessel, null);
                if (actionGroups == null)
                {
                    FieldInfo field = vessel.GetType().GetField("ActionGroups", flags);
                    if (field != null) actionGroups = field.GetValue(vessel);
                }
                if (actionGroups == null) return false;

                foreach (MethodInfo method in actionGroups.GetType().GetMethods(flags))
                {
                    if (!string.Equals(method.Name, "SetGroup", StringComparison.OrdinalIgnoreCase)) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 2) continue;
                    method.Invoke(actionGroups, new object[] { group, enabled });
                    return true;
                }

                // A few KSP builds expose the collection as an indexed
                // property rather than SetGroup. Support that shape too.
                foreach (PropertyInfo indexed in actionGroups.GetType().GetProperties(flags))
                {
                    ParameterInfo[] parameters = indexed.GetIndexParameters();
                    if (parameters.Length != 1 || !indexed.CanWrite) continue;
                    indexed.SetValue(actionGroups, enabled, new object[] { group });
                    return true;
                }
            }
            catch (Exception exception)
            {
                KspMcpBridge.Log("could not set vessel action group " + groupName + ": " + exception.Message);
            }
            return false;
        }

        private static bool TryParseGroup(string name, out KSPActionGroup group)
        {
            string normalised = name;
            if (string.Equals(name, "Lights", StringComparison.OrdinalIgnoreCase)) normalised = "Light";
            try
            {
                group = (KSPActionGroup)Enum.Parse(typeof(KSPActionGroup), normalised, true);
                return true;
            }
            catch (Exception)
            {
                group = KSPActionGroup.None;
                return false;
            }
        }

        private Dictionary<string, object> ControlSnapshot()
        {
            return new Dictionary<string, object>
            {
                { "active", _leaseUntil > 0d && Planetarium.GetUniversalTime() <= _leaseUntil },
                { "seconds_remaining", Math.Max(0d, _leaseUntil - Planetarium.GetUniversalTime()) },
                { "sas", _sasEnabled },
                { "rcs", _rcsEnabled },
                { "throttle", _lease.HasThrottle ? (object)_lease.Throttle : null },
                { "pitch", _lease.HasPitch ? (object)_lease.Pitch : null },
                { "yaw", _lease.HasYaw ? (object)_lease.Yaw : null },
                { "roll", _lease.HasRoll ? (object)_lease.Roll : null }
            };
        }

        private static Dictionary<string, object> CurrentControlState(FlightCtrlState state)
        {
            return new Dictionary<string, object>
            {
                { "throttle", state.mainThrottle },
                { "pitch", state.pitch },
                { "yaw", state.yaw },
                { "roll", state.roll },
                { "translate_x", state.X },
                { "translate_y", state.Y },
                { "translate_z", state.Z },
                { "sas", state.killRot },
                { "gear_up", state.gearUp },
                { "gear_down", state.gearDown },
                { "lights", state.headlight }
            };
        }

        private static Dictionary<string, object> OrbitSummary(Orbit orbit)
        {
            if (orbit == null) return null;
            return new Dictionary<string, object>
            {
                { "reference_body", orbit.referenceBody == null ? null : orbit.referenceBody.bodyName },
                { "semi_major_axis_m", NumberMember(orbit, "semiMajorAxis") },
                { "apoapsis_m", NumberMember(orbit, "ApA") },
                { "periapsis_m", NumberMember(orbit, "PeA") },
                { "eccentricity", NumberMember(orbit, "eccentricity") },
                { "inclination_deg", NumberMember(orbit, "inclination") },
                { "longitude_of_ascending_node_deg", NumberMember(orbit, "LAN") },
                { "argument_of_periapsis_deg", NumberMember(orbit, "argumentOfPeriapsis") },
                { "period_s", NumberMember(orbit, "period") },
                { "epoch_ut", NumberMember(orbit, "epoch") },
                { "mean_anomaly_at_epoch_rad", NumberMember(orbit, "meanAnomalyAtEpoch") },
                { "mean_motion_rad_s", NumberMember(orbit, "meanMotion") },
                { "true_anomaly_rad", NumberMember(orbit, "trueAnomaly") },
                { "time_to_apoapsis_s", NumberMember(orbit, "timeToAp") },
                { "time_to_periapsis_s", NumberMember(orbit, "timeToPe") }
            };
        }

        private static List<object> AggregateResources(Vessel vessel)
        {
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var maximums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Resources == null) continue;
                foreach (PartResource resource in part.Resources)
                {
                    if (resource == null) continue;
                    if (!totals.ContainsKey(resource.resourceName)) totals[resource.resourceName] = 0d;
                    if (!maximums.ContainsKey(resource.resourceName)) maximums[resource.resourceName] = 0d;
                    totals[resource.resourceName] += resource.amount;
                    maximums[resource.resourceName] += resource.maxAmount;
                }
            }
            var result = new List<object>();
            foreach (KeyValuePair<string, double> item in totals)
            {
                result.Add(new Dictionary<string, object>
                {
                    { "name", item.Key }, { "amount", item.Value }, { "max_amount", maximums[item.Key] }
                });
            }
            return result;
        }

        private static List<object> EngineSnapshot(Vessel vessel)
        {
            var result = new List<object>();
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    result.Add(new Dictionary<string, object>
                    {
                        { "part_flight_id", part.flightID },
                        { "stage", part.inverseStage },
                        { "module", module.GetType().Name },
                        { "max_thrust", NumberMember(module, "maxThrust") },
                        { "final_thrust", NumberMember(module, "finalThrust") },
                        { "ignited", BoolMember(module, "engineIgnited") },
                        { "flameout", BoolMember(module, "flameout") },
                        { "operational", BoolMember(module, "isOperational") },
                        { "enabled", BoolMember(module, "isEnabled") },
                        { "module_enabled", BoolMember(module, "moduleIsEnabled") },
                        { "staged", BoolMember(module, "staged") },
                        { "requested_throttle", NumberMember(module, "requestedThrottle") },
                        { "current_throttle", NumberMember(module, "currentThrottle") },
                        { "propellant_requirement", NumberMember(module, "propellantReqMet") }
                    });
                }
            }
            return result;
        }

        private static Dictionary<string, object> EngineSummary(Vessel vessel)
        {
            int count = 0;
            int ignited = 0;
            int operational = 0;
            int flameout = 0;
            int enabled = 0;
            int staged = 0;
            double maxThrust = 0d;
            double finalThrust = 0d;
            if (vessel != null && vessel.parts != null)
            {
                foreach (Part part in vessel.parts)
                {
                    if (part == null || part.Modules == null) continue;
                    foreach (PartModule module in part.Modules)
                    {
                        if (module == null || module.GetType().Name.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        count++;
                        if (BoolMember(module, "engineIgnited")) ignited++;
                        if (BoolMember(module, "isOperational")) operational++;
                        if (BoolMember(module, "flameout")) flameout++;
                        if (BoolMember(module, "moduleIsEnabled") || BoolMember(module, "isEnabled")) enabled++;
                        if (BoolMember(module, "staged")) staged++;
                        maxThrust += Math.Max(0d, NumberMember(module, "maxThrust"));
                        finalThrust += Math.Max(0d, NumberMember(module, "finalThrust"));
                    }
                }
            }
            return new Dictionary<string, object>
            {
                { "staging_cursor", vessel == null ? -1 : vessel.currentStage },
                { "next_stage", vessel == null ? -1 : Math.Max(0, vessel.currentStage - 1) },
                { "count", count },
                { "ignited", ignited },
                { "operational", operational },
                { "flameout", flameout },
                { "enabled", enabled },
                { "staged", staged },
                { "max_thrust_kN", maxThrust },
                { "final_thrust_kN", finalThrust }
            };
        }

        private static List<object> GuidanceStageReport(Vessel vessel)
        {
            var stages = new Dictionary<int, Dictionary<string, object>>();
            if (vessel != null && vessel.parts != null)
            {
                foreach (Part part in vessel.parts)
                {
                    if (part == null || part.Modules == null) continue;
                    int stage = part.inverseStage;
                    Dictionary<string, object> item;
                    if (!stages.TryGetValue(stage, out item))
                    {
                        item = new Dictionary<string, object>
                        {
                            { "stage", stage },
                            { "part_count", 0 },
                            { "engine_count", 0 },
                            { "decoupler_count", 0 },
                            { "ignited_engines", 0 },
                            { "operational_engines", 0 },
                            { "flameout_engines", 0 },
                            { "max_thrust_kN", 0d }
                        };
                        stages[stage] = item;
                    }
                    item["part_count"] = (int)item["part_count"] + 1;
                    foreach (PartModule module in part.Modules)
                    {
                        if (module == null) continue;
                        string moduleName = module.GetType().Name;
                        if (moduleName.IndexOf("ModuleEngines", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            item["engine_count"] = (int)item["engine_count"] + 1;
                            item["max_thrust_kN"] = (double)item["max_thrust_kN"] + Math.Max(0d, NumberMember(module, "maxThrust"));
                            if (BoolMember(module, "engineIgnited")) item["ignited_engines"] = (int)item["ignited_engines"] + 1;
                            if (BoolMember(module, "isOperational")) item["operational_engines"] = (int)item["operational_engines"] + 1;
                            if (BoolMember(module, "flameout")) item["flameout_engines"] = (int)item["flameout_engines"] + 1;
                        }
                        if (moduleName.IndexOf("Decouple", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            moduleName.IndexOf("Separator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            moduleName.IndexOf("Seperator", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            item["decoupler_count"] = (int)item["decoupler_count"] + 1;
                        }
                    }
                }
            }
            var result = new List<object>();
            foreach (Dictionary<string, object> item in stages.Values) result.Add(item);
            result.Sort(delegate(object left, object right)
            {
                return ((int)((Dictionary<string, object>)right)["stage"]).CompareTo((int)((Dictionary<string, object>)left)["stage"]);
            });
            return result;
        }

        private static double NumberMember(object target, string name)
        {
            try
            {
                Type type = target.GetType();
                FieldInfo field = FindField(type, name);
                object value = field == null ? null : field.GetValue(target);
                if (value == null)
                {
                    PropertyInfo property = FindProperty(type, name);
                    value = property == null ? null : property.GetValue(target, null);
                }
                if (value is IConvertible) return Convert.ToDouble(value);
            }
            catch (Exception) { }
            return 0d;
        }

        private static Vector3d VectorMember(object target, string name)
        {
            if (target == null) return Vector3d.zero;
            try
            {
                Type type = target.GetType();
                FieldInfo field = FindField(type, name);
                object value = field == null ? null : field.GetValue(target);
                if (value == null)
                {
                    PropertyInfo property = FindProperty(type, name);
                    value = property == null ? null : property.GetValue(target, null);
                }
                if (value is Vector3d) return (Vector3d)value;
                if (value is Vector3) return (Vector3d)(Vector3)value;
            }
            catch (Exception) { }
            return Vector3d.zero;
        }

        private static bool BoolMember(object target, string name)
        {
            try
            {
                Type type = target.GetType();
                FieldInfo field = FindField(type, name);
                object value = field == null ? null : field.GetValue(target);
                if (value == null)
                {
                    PropertyInfo property = FindProperty(type, name);
                    value = property == null ? null : property.GetValue(target, null);
                }
                return value is bool && (bool)value;
            }
            catch (Exception) { return false; }
        }

        private static string TextMember(object target, string name)
        {
            try
            {
                if (target == null) return null;
                Type type = target.GetType();
                FieldInfo field = FindField(type, name);
                object value = field == null ? null : field.GetValue(target);
                if (value == null)
                {
                    PropertyInfo property = FindProperty(type, name);
                    value = property == null ? null : property.GetValue(target, null);
                }
                return value == null ? null : value.ToString();
            }
            catch (Exception) { return null; }
        }

        private static bool InvokeBoolMethod(object target, string name)
        {
            try
            {
                if (target == null) return false;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                MethodInfo method = target.GetType().GetMethod(name, flags, null, Type.EmptyTypes, null);
                if (method == null) return false;
                object value = method.Invoke(target, null);
                return value is bool && (bool)value;
            }
            catch (Exception) { return false; }
        }

        private static void SetMember(object target, string name, object value)
        {
            try
            {
                Type type = target.GetType();
                FieldInfo field = FindField(type, name);
                if (field != null)
                {
                    field.SetValue(target, ConvertMemberValue(value, field.FieldType));
                    return;
                }
                PropertyInfo property = FindProperty(type, name);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(target, ConvertMemberValue(value, property.PropertyType), null);
                }
            }
            catch (Exception) { }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo exact = type.GetField(name, flags);
            if (exact != null) return exact;
            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)) return field;
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo exact = type.GetProperty(name, flags);
            if (exact != null) return exact;
            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property;
            }
            return null;
        }

        private static object ConvertMemberValue(object value, Type targetType)
        {
            if (targetType == typeof(float)) return Convert.ToSingle(value);
            if (targetType == typeof(double)) return Convert.ToDouble(value);
            if (targetType == typeof(int)) return Convert.ToInt32(value);
            if (targetType == typeof(bool)) return Convert.ToBoolean(value);
            return value;
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double ClampDouble(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}

