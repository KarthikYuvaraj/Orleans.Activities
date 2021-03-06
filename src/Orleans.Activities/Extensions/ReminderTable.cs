﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Activities;
using System.Activities.Hosting;
using System.Xml.Linq;
using Orleans.Activities.AsyncEx;
using Orleans.Activities.Hosting;

namespace Orleans.Activities.Extensions
{
    // TODO investigate the validity of these algorithms
    // TODO if it works, create standalone queues for the states below, because of performance
    // TODO alternative algorithm: register only 1 reminder per workflow (like the original TimerExtensions TimerExpirationTime used by SQL to reload the workflow),
    //      but in this case the registration (before/after save) seems to be tricky to be fail-safe
    //      (if shortens expiration, reregister before save??? if extends expiration, reregister after save??? what about non-persistence zones???)

    // - due to Reminder registration and unregistration is async, we can't register/unregister them in TimerExtension's OnRegisterTimer() and OnCancelTimer()
    // - due to Controller.GetBookmarks() returns only the named bookmarks, we have to save the bookmarks that have associated reminders
    //   to resume them and to recreate the cache described below on load
    // - due to reminder registration and unregistration doesn't happen in one transaction with the state persistence (not like in the original
    //   SqlWorkflowInstanceStore), we have to register reminders before and unregister them after state persistence to be fail-safe
    // - we store the registration/unregistration requests and during the OnPausedAsync, OnSavingAsync, OnSavedAsync "events" we register/unregister them
    // - it has the consequence, that the reminders are not created before the controller/executer gets idle,
    //   but due to single threadedness it wouldn't be able to resume them (these are plain old bookmarks)
    // - we always register them BEFORE save, so in the stored state, the stored controller/executor state and the registered reminders are correlate
    //   the only problem (when controller/executor state persistence aborts after reminders are registered), that maybe reminders are exist but the bookmarks are not
    //   (when the workflow is reloaded from a previous state), in that case we unregister the unnecessary reminders on load or after they fire
    //   this is a problem and extra activity only after serious failure, normal operation is not effected
    // - we always unregister them AFTER save (if cancel requested), so in worst case they will fire unnecessarily (see above)
    // - in case of nonpersistent idle, we also register the reminders (due to the OnPausedAsync "event"), but we can unregister them on idle also,
    //   because they are not saved
    // - the ReminderTable is never persisted with it's actual content, we don't save entries/bookmarks where the associated reminder will be deleted after save!
    // - during save it registers a default reactivation reminder if the controller/executor state is runnable and there are no registered reminders for the instance,
    //   this way in case of failure (when the instance is aborted or the silo is crashed), the reactivation reminder will reactivate the grain/host/instance
    // - after save and load it unregisters the reactivation reminder if the controller/executor state is not runnable (ie. idle or completed)
    //   or there are registered reminders for the instance
    // - when the reactivation reminder fires, it is a no-op, it simply reactivates/reloads the grain/workflow if it's not activated or if it's aborted

    /// <summary>
    /// Used by <see cref="DurableReminderExtension"/>. Handles the persistence and notification (ie. idle) events related registration/unregistration functionality.
    /// </summary>
    public class ReminderTable
    {
        public enum ReminderState : int
        {
            NonExistent = 0,            // not a real, stored state, it is the result of a failed reminders.TryGetValue()
            RegisterAndSave,            // TimerExtension.OnRegisterTimer() called in NonExistent "state" - fresh new reminder, never saved, can be cancelled during OnIdleAsync()
            ReregisterAndResave,        // TimerExtension.OnRegisterTimer() called in SaveAndUnregister state - previously saved, can NOT be cancelled during OnIdleAsync()
            RegisteredButNotSaved,      // registered during OnIdleAsync(), but not saved yet
            RegisteredButNotResaved,    // registered during OnIdleAsync(), but not resaved yet
            RegisteredAndSaved,         // saved after registration
            SaveAndUnregister,          // TimerExtension.OnCancelTimer() called in RegisteredAndSaved state
            Unregister,                 // TimerExtension.OnCancelTimer() called in RegisteredButNotSaved state - never saved, can be cancelled during OnIdleAsync()
        }

        // Transitions:           | Register               Unregister           OnPaused                              OnSaving              OnSaved
        // -----------------------|-----------------------------------------------------------------------------------------------------------------------------------
        // NonExistent            | ->RegisterAndSave      NOOP
        // RegisterAndSave        | ->RegisterAndSave      delete entry         register & ->RegisteredButNotSaved    exception             exception
        // ReregisterAndResave    | ->ReregisterAndResave  ->SaveAndUnregister  register & ->RegisteredButNotResaved  exception             exception
        // RegisteredButNotSaved  | exception              ->Unregister         NOOP                                  ->RegisteredAndSaved  exception
        // RegisteredButNotResaved| exception              ->SaveAndUnregister  NOOP                                  ->RegisteredAndSaved  exception
        // RegisteredAndSaved     | exception              ->SaveAndUnregister  NOOP                                  NOOP                  NOOP
        // SaveAndUnregister      | ->ReregisterAndResave  NOOP                 NOOP                                  NOOP                  unregister & delete entry
        // Unregister             | ->RegisterAndSave      NOOP                 unregister & delete entry             exception             exception

        protected class ReminderInfo
        {
            public Bookmark Bookmark { get; }
            public DateTime DueTime { get; }
            public ReminderState ReminderState { get; set; }

            public ReminderInfo(Bookmark bookmark, ReminderState reminderState)
                : this(bookmark, reminderState, default(DateTime))
            { }

            public ReminderInfo(Bookmark bookmark, ReminderState reminderState, DateTime dueTime)
            {
                Bookmark = bookmark;
                DueTime = dueTime;
                ReminderState = reminderState;
            }
        }

        public static class WorkflowNamespace
        {
            private static readonly XNamespace remindersPath = XNamespace.Get(Persistence.WorkflowNamespace.BaseNamespace + "/reminders");

            private static readonly XName bookmarks= remindersPath.GetName(nameof(Bookmarks));
            public static XName Bookmarks => bookmarks; // used for persistence

            private static readonly XName reactivation = remindersPath.GetName(nameof(Reactivation));
            public static XName Reactivation => reactivation; // used for reactivation reminder naming

            public static readonly string ReminderNameForReactivation = Reactivation.ToString();
            public static readonly string ReminderPrefixForBookmarks = "{" + remindersPath.NamespaceName + "/bookmarks" + "}";
        }

        protected IActivityContext instance;
        protected IDictionary<string, ReminderInfo> reminders;
        protected bool hasReactivationReminder;

        public ReminderTable(IActivityContext instance)
        {
            this.instance = instance;
            this.reminders = new Dictionary<string, ReminderInfo>();
            //this.hasReactivationReminder = false;
        }

        public static bool IsReactivationReminder(string reminderName) =>
            reminderName == WorkflowNamespace.ReminderNameForReactivation;

        public Bookmark GetBookmark(string reminderName)
        {
            ReminderInfo reminderInfo;
            if (reminders.TryGetValue(reminderName, out reminderInfo))
                return reminderInfo.Bookmark;
            return null;
        }

        protected static string CreateReminderName(Bookmark bookmark) =>
            WorkflowNamespace.ReminderPrefixForBookmarks + bookmark.ToString();

        public void RegisterOrUpdateReminder(Bookmark bookmark, TimeSpan dueTime)
        {
            string reminderName = CreateReminderName(bookmark);
            ReminderInfo reminderInfo;
            ReminderState reminderState;
            if (reminders.TryGetValue(reminderName, out reminderInfo))
                reminderState = reminderInfo.ReminderState;
            else
                reminderState = ReminderState.NonExistent;
            switch (reminderState)
            {
                case ReminderState.NonExistent:
                case ReminderState.RegisterAndSave:
                case ReminderState.Unregister:
                    reminders[reminderName] = new ReminderInfo(bookmark, ReminderState.RegisterAndSave, DateTime.UtcNow + dueTime);
                    break;
                case ReminderState.ReregisterAndResave:
                case ReminderState.SaveAndUnregister:
                    reminders[reminderName] = new ReminderInfo(bookmark, ReminderState.ReregisterAndResave, DateTime.UtcNow + dueTime);
                    break;
                //case ReminderState.RegisteredButNotSaved:
                //case ReminderState.RegisteredButNotResaved:
                //case ReminderState.RegisteredAndSaved:
                default:
                    throw new InvalidOperationException($"Reminder '{reminderName}' can't be registered in state '{reminderState}'.");
            }
        }

        public void UnregisterReminder(Bookmark bookmark)
        {
            UnregisterReminder(CreateReminderName(bookmark));
        }

        public void UnregisterReminder(string reminderName)
        {
            ReminderInfo reminderInfo;
            ReminderState reminderState;
            if (reminders.TryGetValue(reminderName, out reminderInfo))
                reminderState = reminderInfo.ReminderState;
            else
                reminderState = ReminderState.NonExistent;
            switch (reminderState)
            {
                case ReminderState.NonExistent:
                case ReminderState.SaveAndUnregister:
                case ReminderState.Unregister:
                    break;
                case ReminderState.RegisterAndSave:
                    reminders.Remove(reminderName);
                    break;
                case ReminderState.ReregisterAndResave:
                case ReminderState.RegisteredButNotResaved:
                case ReminderState.RegisteredAndSaved:
                    reminderInfo.ReminderState = ReminderState.SaveAndUnregister;
                    break;
                case ReminderState.RegisteredButNotSaved:
                    reminderInfo.ReminderState = ReminderState.Unregister;
                    break;
                default:
                    throw new InvalidOperationException($"Reminder '{reminderName}' can't be unregistered in state '{reminderState}'.");
            }
        }

        public async Task OnPausedAsync()
        {
            foreach (KeyValuePair<string, ReminderInfo> kvp in reminders
                .Where((kvp) =>
                    kvp.Value.ReminderState != ReminderState.RegisteredButNotSaved
                    && kvp.Value.ReminderState != ReminderState.RegisteredButNotResaved
                    && kvp.Value.ReminderState != ReminderState.RegisteredAndSaved
                    && kvp.Value.ReminderState != ReminderState.SaveAndUnregister)
                .ToList()) // because we will modify the dictionary
                switch (kvp.Value.ReminderState)
                {
                    case ReminderState.RegisterAndSave:
                        kvp.Value.ReminderState = ReminderState.RegisteredButNotSaved;
                        await instance.RegisterOrUpdateReminderAsync(kvp.Key, kvp.Value.DueTime - DateTime.UtcNow);
                        break;
                    case ReminderState.ReregisterAndResave:
                        kvp.Value.ReminderState = ReminderState.RegisteredButNotResaved;
                        await instance.RegisterOrUpdateReminderAsync(kvp.Key, kvp.Value.DueTime - DateTime.UtcNow);
                        break;
                    case ReminderState.Unregister:
                        reminders.Remove(kvp.Key);
                        await instance.UnregisterReminderAsync(kvp.Key);
                        break;
                    //case ReminderState.RegisteredButNotSaved:
                    //case ReminderState.RegisteredButNotResaved:
                    default:
                        throw new InvalidOperationException($"Reminder '{kvp.Key}' is in state '{kvp.Value.ReminderState}' during OnPaused.");
                }
        }

        protected static bool ShouldCollect(ReminderState reminderState) =>
            reminderState == ReminderState.RegisteredButNotSaved
            || reminderState == ReminderState.RegisteredButNotResaved
            || reminderState == ReminderState.RegisteredAndSaved;

        public void CollectValues(out IDictionary<XName, object> readWriteValues, out IDictionary<XName, object> writeOnlyValues)
        {
            readWriteValues = null;
            writeOnlyValues = null;

            if (reminders.Count > 0)
            {
                readWriteValues = new Dictionary<XName, object>(1);
                readWriteValues.Add(
                    WorkflowNamespace.Bookmarks,
                    // do not save bookmarks where the associated reminder will be deleted after save
                    reminders.Where((kvp) => ShouldCollect(kvp.Value.ReminderState)).Select((kvp) => kvp.Value.Bookmark).ToList());
            }
        }

        protected Task RegisterReactivationReminderIfRequired()
        {
            if (instance.WorkflowInstanceState == WorkflowInstanceState.Runnable
                && !reminders.Where((kvp) => kvp.Value.ReminderState == ReminderState.RegisteredAndSaved).Any())
            {
                // always update it on each save, not just when not yet registered
                hasReactivationReminder = true;
                return instance.RegisterOrUpdateReminderAsync(WorkflowNamespace.ReminderNameForReactivation, instance.Parameters.ReactivationReminderPeriod);
            }
            else
                return TaskConstants.Completed;
        }

        protected Task UnregisterReactivationReminderIfNotRequired()
        {
            if (hasReactivationReminder
                && (instance.WorkflowInstanceState != WorkflowInstanceState.Runnable
                    || reminders.Where((kvp) => kvp.Value.ReminderState == ReminderState.RegisteredAndSaved).Any()))
            {
                hasReactivationReminder = false;
                return instance.UnregisterReminderAsync(WorkflowNamespace.ReminderNameForReactivation);
            }
            else
                return TaskConstants.Completed;
        }

        public Task OnSavingAsync()
        {
            foreach (KeyValuePair<string, ReminderInfo> kvp in reminders
                .Where((kvp) => kvp.Value.ReminderState != ReminderState.RegisteredAndSaved && kvp.Value.ReminderState != ReminderState.SaveAndUnregister))
                switch (kvp.Value.ReminderState)
                {
                    case ReminderState.RegisteredButNotSaved:
                    case ReminderState.RegisteredButNotResaved:
                        kvp.Value.ReminderState = ReminderState.RegisteredAndSaved;
                        break;
                    //case ReminderState.RegisterAndSave:
                    //case ReminderState.ReregisterAndResave:
                    //case ReminderState.Unregister:
                    default:
                        throw new InvalidOperationException($"Reminder '{kvp.Key}' is in state '{kvp.Value.ReminderState}' during OnSaving.");
                }

            return RegisterReactivationReminderIfRequired();
        }

        public async Task OnSavedAsync()
        {
            foreach (KeyValuePair<string, ReminderInfo> kvp in reminders
                .Where((kvp) => kvp.Value.ReminderState != ReminderState.RegisteredAndSaved)
                .ToList()) // because we will modify the dictionary
                switch (kvp.Value.ReminderState)
                {
                    case ReminderState.SaveAndUnregister:
                        reminders.Remove(kvp.Key);
                        await instance.UnregisterReminderAsync(kvp.Key);
                        break;
                    //case ReminderState.RegisterAndSave:
                    //case ReminderState.ReregisterAndResave:
                    //case ReminderState.RegisteredButNotSaved:
                    //case ReminderState.RegisteredButNotResaved:
                    //case ReminderState.Unregister:
                    default:
                        throw new InvalidOperationException($"Reminder '{kvp.Key}' is in state '{kvp.Value.ReminderState}' during OnSaved.");
                }

            await UnregisterReactivationReminderIfNotRequired();
        }

        public async Task LoadAsync(IDictionary<XName, object> readWriteValues)
        {
            reminders.Clear();
            hasReactivationReminder = false;

            object reminderBookmarks;
            if (readWriteValues != null && readWriteValues.TryGetValue(WorkflowNamespace.Bookmarks, out reminderBookmarks))
                foreach (Bookmark reminderBookmark in (reminderBookmarks as List<Bookmark>))
                    reminders[CreateReminderName(reminderBookmark)] = new ReminderInfo(reminderBookmark, ReminderState.RegisteredAndSaved);
            foreach (string reminderName in await instance.GetRemindersAsync())
                if (reminderName == WorkflowNamespace.ReminderNameForReactivation)
                    hasReactivationReminder = true;
                else if (reminderName.StartsWith(WorkflowNamespace.ReminderPrefixForBookmarks) // there can be other reminders
                    && !reminders.ContainsKey(reminderName))
                    await instance.UnregisterReminderAsync(reminderName);

            await UnregisterReactivationReminderIfNotRequired();
        }
    }
}
