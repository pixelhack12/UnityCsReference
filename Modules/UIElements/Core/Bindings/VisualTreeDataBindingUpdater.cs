// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.Properties;
using UnityEngine.Assertions;

namespace UnityEngine.UIElements
{
    enum BindingUpdateStage
    {
        UpdateUI,
        UpdateSource
    }

    class VisualTreeDataBindingsUpdater : BaseVisualTreeHierarchyTrackerUpdater
    {
        public long frame { get; private set; }

        readonly struct VersionInfo
        {
            public readonly object source;
            public readonly long version;

            public VersionInfo(object source, long version)
            {
                this.source = source;
                this.version = version;
            }
        }

        static readonly ProfilerMarker s_UpdateProfilerMarker = new ProfilerMarker("Update Runtime Bindings");
        static readonly ProfilerMarker s_ProcessBindingRequestsProfilerMarker = new ProfilerMarker("Process Binding Requests");
        static readonly ProfilerMarker s_ProcessDataSourcesProfilerMarker = new ProfilerMarker("Process Data Sources");
        static readonly ProfilerMarker s_ShouldUpdateBindingProfilerMarker = new ProfilerMarker("Should Update Binding");
        static readonly ProfilerMarker s_UpdateBindingProfilerMarker = new ProfilerMarker("Update Binding");

        DataBindingManager bindingManager => panel.dataBindingManager;

        public override ProfilerMarker profilerMarker => s_UpdateProfilerMarker;

        readonly BindingUpdater m_Updater = new BindingUpdater();
        readonly List<VisualElement> m_BindingRegistrationRequests = new List<VisualElement>();
        readonly HashSet<VisualElement> m_DataSourceChangedRequests = new HashSet<VisualElement>();
        readonly HashSet<VisualElement> m_RemovedElements = new HashSet<VisualElement>();

        protected override void OnHierarchyChange(VisualElement ve, HierarchyChangeType type)
        {
            // Invalidating cached data sources can do up to a full hierarchy traversal, so if nothing is registered, we
            // can safely skip the invalidation step completely.
            if (bindingManager.GetBoundElementsCount() == 0 && bindingManager.GetTrackedDataSourcesCount() == 0)
                return;

            switch (type)
            {
                // We need to treat removed elements differently. If an element is removed, we need to force a full
                // invalidation, unless we can detect that it's still part of the hierarchy (this can happen if structural
                // changes happened outside of the panel).
                case HierarchyChangeType.Remove:
                    m_DataSourceChangedRequests.Remove(ve);
                    m_RemovedElements.Add(ve);
                    break;
                // If the element was previously removed and then added, treat it as if it had moved, so we need to
                // invalidate it doing minimal work.
                case HierarchyChangeType.Add:
                    m_RemovedElements.Remove(ve);
                    m_DataSourceChangedRequests.Add(ve);
                    break;
                default:
                    m_DataSourceChangedRequests.Add(ve);
                    break;
            }

            bindingManager.DirtyBindingOrder();
        }

        public override void OnVersionChanged(VisualElement ve, VersionChangeType versionChangeType)
        {
            base.OnVersionChanged(ve, versionChangeType);

            if ((versionChangeType & VersionChangeType.BindingRegistration) == VersionChangeType.BindingRegistration)
                m_BindingRegistrationRequests.Add(ve);

            if ((versionChangeType & VersionChangeType.DataSource) == VersionChangeType.DataSource)
                m_DataSourceChangedRequests.Add(ve);
        }

        void CacheAndLogBindingResult(bool appliedOnUiCache, in DataBindingManager.BindingData bindingData, in BindingResult result)
        {
            var logLevel = bindingManager.logLevel;

            if (logLevel == BindingLogLevel.None)
            {
                // Log nothing.
            }
            else if (logLevel == BindingLogLevel.Once)
            {
                BindingResult previousResult;
                if (appliedOnUiCache)
                    bindingManager.TryGetLastUIBindingResult(bindingData, out previousResult);
                else
                    bindingManager.TryGetLastSourceBindingResult(bindingData, out previousResult);

                if (previousResult.status != result.status || previousResult.message != result.message)
                {
                    LogResult(result);
                }
            }
            else
            {
                LogResult(result);
            }

            if (appliedOnUiCache)
                bindingManager.CacheUIBindingResult(bindingData, result);
            else
                bindingManager.CacheSourceBindingResult(bindingData, result);
        }

        void LogResult(in BindingResult result)
        {
            if (string.IsNullOrWhiteSpace(result.message))
                return;

            var panelName = (panel as Panel)?.name ?? panel.visualTree.name;
            Debug.LogWarning($"{result.message} ({panelName})");
        }

        private readonly List<VisualElement> m_BoundsElement = new List<VisualElement>();
        private readonly List<VersionInfo> m_VersionChanges = new List<VersionInfo>();
        private readonly HashSet<object> m_TrackedObjects = new HashSet<object>();
        private readonly HashSet<Binding> m_RanUpdate = new HashSet<Binding>();
        private readonly HashSet<object> m_KnownSources = new HashSet<object>();

        public override void Update()
        {
            ++frame;
            base.Update();

            ProcessAllBindingRequests();
            ProcessDataSourceChangedRequests();

            ProcessPropertyChangedEvents(m_RanUpdate);

            m_BoundsElement.AddRange(bindingManager.GetBoundElements());
            foreach (var element in m_BoundsElement)
            {
                var bindings = bindingManager.GetBindingData(element);
                for (var i = 0; i < bindings.Count; ++i)
                {
                    var bindingData = bindings[i];
                    PropertyPath resolvedDataSourcePath;
                    object source;
                    using (s_ShouldUpdateBindingProfilerMarker.Auto())
                    {
                        var resolvedContext = bindingManager.GetResolvedDataSourceContext(element, bindingData);
                        source = resolvedContext.dataSource;
                        resolvedDataSourcePath = resolvedContext.dataSourcePath;

                        var (changed, version) = GetDataSourceVersion(source);

                        // We want to track the earliest version of the source, in case one of the bindings changes it
                        if (null != source && m_TrackedObjects.Add(source))
                            m_VersionChanges.Add(new VersionInfo(source, version));

                        if (!m_Updater.ShouldProcessBindingAtStage(bindingData.binding, BindingUpdateStage.UpdateUI, changed))
                        {
                            continue;
                        }

                        if (source is INotifyBindablePropertyChanged && !bindingData.binding.isDirty)
                        {
                            var changedPaths = bindingManager.GetChangedDetectedFromSource(source);
                            if (null == changedPaths || changedPaths.Count == 0)
                                continue;

                            var processBinding = false;

                            foreach (var path in changedPaths)
                            {
                                if (IsPrefix(path, resolvedDataSourcePath))
                                {
                                    processBinding = true;
                                    break;
                                }
                            }

                            if (!processBinding)
                                continue;
                        }
                    }

                    if (null != source)
                        m_KnownSources.Add(source);

                    var wasDirty = bindingData.binding.isDirty;
                    bindingData.binding.ClearDirty();

                    var context = new BindingContext
                    (
                        element,
                        bindingData.target.bindingId,
                        resolvedDataSourcePath,
                        source
                    );

                    BindingResult result = default;
                    using (s_UpdateBindingProfilerMarker.Auto())
                    {
                        result = m_Updater.UpdateUI(in context, bindingData.binding);
                    }

                    CacheAndLogBindingResult(true, bindingData, result);

                    switch (result.status)
                    {
                        case BindingStatus.Success:
                            m_RanUpdate.Add(bindingData.binding);
                            break;
                        case BindingStatus.Pending when wasDirty:
                            bindingData.binding.MarkDirty();
                            break;
                        case BindingStatus.Pending:
                            // Intentionally left empty.
                            break;
                    }
                }
            }

            foreach (var versionInfo in m_VersionChanges)
            {
                bindingManager.UpdateVersion(versionInfo.source, versionInfo.version);
            }

            ProcessPropertyChangedEvents(m_RanUpdate);

            foreach (var touchedSource in m_KnownSources)
            {
                bindingManager.ClearChangesFromSource(touchedSource);
            }

            m_BoundsElement.Clear();
            m_VersionChanges.Clear();
            m_TrackedObjects.Clear();
            m_RanUpdate.Clear();
            m_KnownSources.Clear();

            bindingManager.ClearSourceCache();
        }

        private (bool changed, long version) GetDataSourceVersion(object source)
        {
            if (bindingManager.TryGetLastVersion(source, out var version))
            {
                // If the data source is not versioned, we touch the version every update to keep it "fresh"
                if (source is not IDataSourceViewHashProvider versioned)
                    return (true, version + 1);

                var currentVersion = versioned.GetViewHashCode();

                // Version didn't change, no need to update the UI
                return currentVersion == version ? (false, version) : (true, currentVersion);
            }
            else if (source is IDataSourceViewHashProvider versioned)
            {
                return (true, versioned.GetViewHashCode());
            }

            return (true, 0L);
        }

        private bool IsPrefix(in PropertyPath prefix, in PropertyPath path)
        {
            if (path.Length < prefix.Length)
                return false;

            for (var i = 0; i < prefix.Length; ++i)
            {
                var prefixPart = prefix[i];
                var part = path[i];

                if (prefixPart.Kind != part.Kind)
                    return false;

                switch (prefixPart.Kind)
                {
                    case PropertyPathPartKind.Name:
                        if (prefixPart.Name != part.Name)
                            return false;
                        break;
                    case PropertyPathPartKind.Index:
                        if (prefixPart.Index != part.Index)
                            return false;
                        break;
                    case PropertyPathPartKind.Key:
                        if (prefixPart.Key != part.Key)
                            return false;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return true;
        }

        void ProcessDataSourceChangedRequests()
        {
            using var marker = s_ProcessDataSourcesProfilerMarker.Auto();
            if (m_DataSourceChangedRequests.Count == 0 && m_RemovedElements.Count == 0)
                return;

            // skip elements that don't belong here. This can happen when a binding request happen while
            // an element is inside a panel, but then removed before this updater can run.
            m_DataSourceChangedRequests.RemoveWhere(e => null == e.panel);

            bindingManager.InvalidateCachedDataSource(m_DataSourceChangedRequests, m_RemovedElements);
            m_DataSourceChangedRequests.Clear();
            m_RemovedElements.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            bindingManager.Dispose();
        }

        void ProcessAllBindingRequests()
        {
            using var marker = s_ProcessBindingRequestsProfilerMarker.Auto();

            for (var i = 0; i < m_BindingRegistrationRequests.Count; ++i)
            {
                var element = m_BindingRegistrationRequests[i];
                // skip elements that don't belong here. This can happen when a binding request happen while
                // an element is inside a panel, but then removed before this updater can run.
                if (null == element.panel)
                    continue;

                Assert.IsTrue(element.panel == panel);

                ProcessBindingRequests(element);
            }

            m_BindingRegistrationRequests.Clear();
        }

        void ProcessBindingRequests(VisualElement element)
        {
            bindingManager.ProcessBindingRequests(element);
        }

        void ProcessPropertyChangedEvents(HashSet<Binding> ranUpdate)
        {
            var data = bindingManager.GetChangedDetectedFromUI();

            for (var index = 0; index < data.Count; index++)
            {
                var change = data[index];
                if (!change.IsValid)
                    continue;

                var bindingData = change.bindingData;
                var binding = bindingData.binding;

                var element = bindingData.target.element;

                if (!m_Updater.ShouldProcessBindingAtStage(binding, BindingUpdateStage.UpdateSource, true))
                    continue;

                if (ranUpdate.Contains(binding))
                    continue;


                var resolvedContext = bindingManager.GetResolvedDataSourceContext(bindingData.target.element, bindingData);
                var source = resolvedContext.dataSource;
                var resolvedSourcePath = resolvedContext.dataSourcePath;

                var toDataSourceContext = new BindingContext
                (
                    element,
                    bindingData.target.bindingId,
                    resolvedSourcePath,
                    source
                );
                var result = m_Updater.UpdateSource(in toDataSourceContext, binding);
                CacheAndLogBindingResult(false, bindingData, result);

                if (result.status == BindingStatus.Success)
                {
                    // Binding was unregistered during the update.
                    if (!change.IsValid)
                        continue;

                    var wasDirty = bindingData.binding.isDirty;
                    bindingData.binding.ClearDirty();

                    var context = new BindingContext
                    (
                        element,
                        bindingData.target.bindingId,
                        resolvedSourcePath,
                        source
                    );
                    result = m_Updater.UpdateUI(in context, binding);
                    CacheAndLogBindingResult(true, bindingData, result);

                    if (result.status == BindingStatus.Pending)
                    {
                        if (wasDirty)
                            bindingData.binding.MarkDirty();
                        else
                            bindingData.binding.ClearDirty();
                    }
                }
            }

            data.Clear();
        }
    }
}
