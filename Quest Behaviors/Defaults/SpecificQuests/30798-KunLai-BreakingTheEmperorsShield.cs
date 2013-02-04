// Behavior originally contributed by Chinajade.
//
// LICENSE:
// This work is licensed under the
//     Creative Commons Attribution-NonCommercial-ShareAlike 3.0 Unported License.
// also known as CC-BY-NC-SA.  To view a copy of this license, visit
//      http://creativecommons.org/licenses/by-nc-sa/3.0/
// or send a letter to
//      Creative Commons // 171 Second Street, Suite 300 // San Francisco, California, 94105, USA.

#region Summary and Documentation
// QUICK DOX:
// 30798-BreakingTheEmporersShield is a point-solution behavior that takes care
// of moving to the Emporer and killing him.
// It prioritizes the spawned mobs when the shield is present.
// 
// EXAMPLE:
//     <CustomBehavior File="30798-BreakingTheEmporersShield" />
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;

using CommonBehaviors.Actions;
using Styx;
using Styx.Common;
using Styx.CommonBot;
using Styx.CommonBot.Frames;
using Styx.CommonBot.POI;
using Styx.CommonBot.Profiles;
using Styx.CommonBot.Routines;
using Styx.Helpers;
using Styx.Pathing;
using Styx.TreeSharp;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

using Action = Styx.TreeSharp.Action;
#endregion


namespace Honorbuddy.QuestBehaviors.BreakingTheEmperorsShield
{
    public class BreakingTheEmperorsShield : CustomForcedBehavior
    {
        public delegate WoWPoint LocationDelegate(object context);
        public delegate string MessageDelegate(object context);
        public delegate double RangeDelegate(object context);

        #region Consructor and Argument Processing
        public BreakingTheEmperorsShield(Dictionary<string, string> args)
            : base(args)
        {
            try
            {
                AvoidTargetsWithAura = new int[] { 118596 /*Protection of Zian*/ };
                QuestId = 30798;
                StartLocation = new WoWPoint(3463.548, 1527.291, 814.9634);
                TargetIds = new int[] { 60572 /*Nakk'rakas*/ };
                QuestRequirementComplete = QuestCompleteRequirement.NotComplete;
                QuestRequirementInLog = QuestInLogRequirement.InLog;
            }

            catch (Exception except)
            {
                // Maintenance problems occur for a number of reasons.  The primary two are...
                // * Changes were made to the behavior, and boundary conditions weren't properly tested.
                // * The Honorbuddy core was changed, and the behavior wasn't adjusted for the new changes.
                // In any case, we pinpoint the source of the problem area here, and hopefully it can be quickly
                // resolved.
                LogMessage("error", "BEHAVIOR MAINTENANCE PROBLEM: " + except.Message
                                    + "\nFROM HERE:\n"
                                    + except.StackTrace + "\n");
                IsAttributeProblem = true;
            }
        }


        // Variables for Attributes provided by caller
        private int[] AvoidTargetsWithAura { get; set; }
        private WoWPoint StartLocation { get; set; }
        private int[] TargetIds { get; set; }

        private int QuestId { get; set; }
        private QuestCompleteRequirement QuestRequirementComplete { get; set; }
        private QuestInLogRequirement QuestRequirementInLog { get; set; }

        // DON'T EDIT THESE--they are auto-populated by Subversion
        public override string SubversionId { get { return "$Id$"; } }
        public override string SubversionRevision { get { return "$Rev$"; } }
        #endregion


        #region Private and Convenience variables

        private readonly TimeSpan Delay_WoWClientMovementThrottle = TimeSpan.FromMilliseconds(100);
        private LocalPlayer Me { get { return StyxWoW.Me; } }
        private IEnumerable<WoWUnit> MeAsGroup  = new List<WoWUnit>() { StyxWoW.Me };

        private Composite _behaviorTreeCombatHook = null;
        private Composite _behaviorTreeMainRoot = null;
        private ConfigMemento _configMemento = null;
        private bool _isBehaviorDone = false;
        private bool _isDisposed = false;
        private WoWUnit _targetPoiUnit = null;
        #endregion


        #region Destructor, Dispose, and cleanup
        ~BreakingTheEmperorsShield()
        {
            Dispose(false);
        }


        public void Dispose(bool isExplicitlyInitiatedDispose)
        {
            if (!_isDisposed)
            {
                // NOTE: we should call any Dispose() method for any managed or unmanaged
                // resource, if that resource provides a Dispose() method.

                // Clean up managed resources, if explicit disposal...
                if (isExplicitlyInitiatedDispose)
                {
                    // empty, for now
                }

                // Clean up unmanaged resources (if any) here...
                if (_behaviorTreeCombatHook != null)
                    { TreeHooks.Instance.RemoveHook("Combat_Main", _behaviorTreeCombatHook); }

                if (_configMemento != null)
                    { _configMemento.Dispose(); }
                
                _configMemento = null;

                BotEvents.OnBotStop -= BotEvents_OnBotStop;

                TreeRoot.GoalText = string.Empty;
                TreeRoot.StatusText = string.Empty;

                // Call parent Dispose() (if it exists) here ...
                base.Dispose();
            }

            _isDisposed = true;
        }


        public void BotEvents_OnBotStop(EventArgs args)
        {
            Dispose();
        }
        #endregion


        #region Overrides of CustomForcedBehavior
        // NB: This behavior is designed to run at a 'higher priority' than Combat_Main.
        // This is necessary because movement is frequently more important than combat when conducting
        // escorts (e.g., the escort doesn't wait for us to kill the mob).  Be aware that this priority
        // inversion is not what you are used to seeing in most quest behaviors, and results in some
        // 'different' combinations of PrioritySelector and Sequence.
        // NB: Due to the complexity, this behavior is also 'state' based.  All necessary actions are
        // conducted in the current state.  If the current state is no longer valid, then a state change
        // is effected.  Ths entry state is "MovingToStartLocation".
        protected override Composite CreateBehavior()
        {
            return _behaviorTreeMainRoot ?? (_behaviorTreeMainRoot = CreateMainBehavior());
        }


        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


        public override bool IsDone
        {
            get
            {
                return _isBehaviorDone     // normal completion
                        || !UtilIsProgressRequirementsMet(QuestId, QuestRequirementInLog, QuestRequirementComplete);
            }
        }


        public override void OnStart()
        {
            PlayerQuest quest = StyxWoW.Me.QuestLog.GetQuestById((uint)QuestId);

            if ((QuestId != 0) && (quest == null))
            {
                LogMessage("error", "This behavior has been associated with QuestId({0}), but the quest is not in our log", QuestId);
                IsAttributeProblem = true;
            }

            // This reports problems, and stops BT processing if there was a problem with attributes...
            // We had to defer this action, as the 'profile line number' is not available during the element's
            // constructor call.
            OnStart_HandleAttributeProblem();

            // If the quest is complete, this behavior is already done...
            // So we don't want to falsely inform the user of things that will be skipped.
            if (!IsDone)
            {
                // The ConfigMemento() class captures the user's existing configuration.
                // After its captured, we can change the configuration however needed.
                // When the memento is dispose'd, the user's original configuration is restored.
                // More info about how the ConfigMemento applies to saving and restoring user configuration
                // can be found here...
                //     http://www.thebuddyforum.com/mediawiki/index.php?title=Honorbuddy_Programming_Cookbook:_Saving_and_Restoring_User_Configuration
                _configMemento = new ConfigMemento();

                BotEvents.OnBotStop += BotEvents_OnBotStop;

                // Disable any settings that may interfere with the escort --
                // When we escort, we don't want to be distracted by other things.
                // NOTE: these settings are restored to their normal values when the behavior completes
                // or the bot is stopped.
                CharacterSettings.Instance.PullDistance = 25;
                GlobalSettings.Instance.KillBetweenHotspots = true;
                
                TreeRoot.GoalText = string.Format(
                    "{0}: \"{1}\"",
                    this.GetType().Name,
                    ((quest != null) ? ("\"" + quest.Name + "\"") : "In Progress (no associated quest)"));

                _behaviorTreeCombatHook = CreateCombatBehavior();
                TreeHooks.Instance.InsertHook("Combat_Main", 0, _behaviorTreeCombatHook);
            }
        }
        #endregion


        #region Main Behavior
        protected Composite CreateCombatBehavior()
        {
            // NB: This behavior is hooked in at a 'higher priority' than Combat_Main.  We need this
            // for proper target selection.

            return new Decorator(context => Me.Combat && HasAuraToBeAvoided(Me.CurrentTarget),
                    new Action(context =>
                    {
                        TreeRoot.StatusText = "NEW TARGET";
                        ChooseBestTarget();
                        return RunStatus.Failure;
                    }));
        }


        protected Composite CreateMainBehavior()
        {
            // Move to destination...
            return new PrioritySelector(
                UtilityBehavior_MoveWithinRange(preferredUnitsContext => StartLocation,
                                                preferredUnitsContext => "start location")
                );
        }


        // Get the weakest mob attacking our weakest escorted unit...
        private void ChooseBestTarget()
        {
            // If we're targetting unit with aura to be avoided, find another target...
            if (HasAuraToBeAvoided(Me.CurrentTarget))
            {
                // Since an aura can go up at any time, we need to constantly evaluate it <sigh>...
                _targetPoiUnit =
                    ObjectManager.GetObjectsOfType<WoWUnit>(true, false)
                    .Where(u => u.IsValid && u.IsHostile && u.Aggro)
                    .OrderBy(u => 
                        (HasAuraToBeAvoided(u) ? 1000 : 1)  // favor targets without aura
                        * u.Distance                        // favor nearby units
                        * (u.Elite ? 100 : 1))              // prefer non-elite mobs
                    .FirstOrDefault();
            }

            // If target has strayed, reset to what we want...
            if ((_targetPoiUnit != null) && (Me.CurrentTarget != _targetPoiUnit))
            {
                Utility_NotifyUser("Selecting new target: {0}", _targetPoiUnit.Name);
                BotPoi.Current = new BotPoi(_targetPoiUnit, PoiType.Kill);
                _targetPoiUnit.Target();
            }
        }


        private bool HasAuraToBeAvoided(WoWUnit wowUnit)
        {
            return (wowUnit == null) ? false : wowUnit.ActiveAuras.Values.Any(a => AvoidTargetsWithAura.Contains(a.SpellId));
        }


        private IEnumerable<WoWUnit> FindUnitsFromIds(IEnumerable<int> unitIds)
        {
            return ObjectManager.GetObjectsOfType<WoWUnit>(true, false)
                .Where(u => unitIds.Contains((int)u.Entry) && u.IsValid && u.IsAlive)
                .ToList();
        }


        // returns true, if any member of GROUP (or their pets) is in combat
        private bool IsInCombat(IEnumerable<WoWUnit> group)
        {
            return group.Any(u => u.Combat || ((u.Pet != null) && u.Pet.Combat));
        }


        private bool IsViableTarget(WoWUnit wowUnit)
        {
            return ((wowUnit != null) && wowUnit.IsValid && wowUnit.IsHostile && !wowUnit.IsDead);
        }

        // Returns: RunStatus.Success while movement is in progress; othwerise, RunStatus.Failure if no movement necessary
        private Composite UtilityBehavior_MoveWithinRange(LocationDelegate locationDelegate,
                                                            MessageDelegate locationNameDelegate,
                                                            RangeDelegate precisionDelegate = null)
        {
            precisionDelegate = precisionDelegate ?? (context => Navigator.PathPrecision);

            return new Sequence(
                // Done, if we're already at destination...
                new DecoratorContinue(context => (Me.Location.Distance(locationDelegate(context)) <= precisionDelegate(context)),
                    new Decorator(context => Me.IsMoving,   // This decorator failing indicates the behavior is complete
                        new Action(delegate { WoWMovement.MoveStop(); }))),

                // Notify user of progress...
                new CompositeThrottle(TimeSpan.FromSeconds(1),
                    new Action(context =>
                    {
                        double destinationDistance = Me.Location.Distance(locationDelegate(context));
                        string locationName = locationNameDelegate(context) ?? locationDelegate(context).ToString();
                        Utility_NotifyUser(string.Format("Moving to {0} (distance: {1:F1})", locationName, destinationDistance));
                    })),

                new Action(context =>
                {
                    WoWPoint destination = locationDelegate(context);

                    // Try to use Navigator to get there...
                    MoveResult moveResult = Navigator.MoveTo(destination);

                    // If Navigator fails, fall back to click-to-move...
                    if ((moveResult == MoveResult.Failed) || (moveResult == MoveResult.PathGenerationFailed))
                        { WoWMovement.ClickToMove(destination); }

                    return RunStatus.Success; // fall through
                }),

                new WaitContinue(Delay_WoWClientMovementThrottle, ret => false, new ActionAlwaysSucceed())
                );
        }


        private void Utility_NotifyUser(string format, params object[] args)
        {
            if (format != null)
            {
                string message = string.Format(format, args);

                if (TreeRoot.StatusText != message)
                    { TreeRoot.StatusText = message; }
            }
        }
        #endregion // Behavior helpers


        #region TreeSharp Extensions
        public class CompositeThrottle : DecoratorContinue
        {
            public CompositeThrottle(TimeSpan throttleTime,
                                     Composite composite)
                : base(composite)
            {
                _throttle = new Stopwatch();
                _throttleTime = throttleTime;
            }


            protected override bool CanRun(object context)
            {
                if (_throttle.IsRunning && (_throttle.Elapsed < _throttleTime))
                    { return false; }

                _throttle.Restart();
                return true;
            }

            private readonly Stopwatch _throttle;
            private readonly TimeSpan _throttleTime;
        }
        #endregion
    }
}
