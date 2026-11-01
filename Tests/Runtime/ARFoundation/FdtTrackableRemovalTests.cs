// FDT patch: removal owns registration even when Unity skips OnDestroy for never-active trackables.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.XR.CoreUtils;
using UnityEngine.TestTools;
using UnityEngine.XR.ARSubsystems;

namespace UnityEngine.XR.ARFoundation.Tests
{
    public sealed class FdtTrackableRemovalTests
    {
        private const string DescriptorId = "FDT-Probe-Removal-Test";
        private static readonly TrackableId ProbeId = new TrackableId(89, 1);
        private GameObject _root;
        private FdtManualProbeTestManager _manager;
        private XREnvironmentProbeSubsystem _subsystem;
        private bool _ownsSpawner;

        [OneTimeSetUp]
        public void RegisterDescriptor()
        {
            XREnvironmentProbeSubsystemDescriptor.Register(new XREnvironmentProbeSubsystemDescriptor.Cinfo
            {
                id = DescriptorId,
                providerType = typeof(RemovalProvider),
                subsystemTypeOverride = typeof(RemovalSubsystem)
            });
        }

        [SetUp]
        public void SetUp()
        {
            Assert.That(UnityEngine.Object.FindAnyObjectByType<XROrigin>(), Is.Null);
            _root = new GameObject("Trackable removal fixture");
            _root.AddComponent<XROrigin>();
            _manager = _root.AddComponent<FdtManualProbeTestManager>();
            _ownsSpawner = true;
            var descriptors = new List<XREnvironmentProbeSubsystemDescriptor>();
            SubsystemManager.GetSubsystemDescriptors(descriptors);
            foreach (var descriptor in descriptors)
                if (descriptor.id == DescriptorId) _subsystem = descriptor.Create();
            Assert.That(_subsystem, Is.Not.Null);
            RemovalProvider.RemoveNext = false;
            RemovalProvider.AddNext = false;
            _subsystem.Start();
            _manager.UseSubsystem(_subsystem);
        }

        [UnityTest]
        public IEnumerator ProviderRemoval_ReleasesAnExternallyDestroyedNeverActiveProbe_AndAllowsIdReuse()
        {
            var probe = Register(false);
            UnityEngine.Object.Destroy(probe.gameObject);
            yield return null;
            Assert.That(probe == null, Is.True);
            // Unity skipped OnDestroy; the provider removal must retire this entry.
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId.ContainsKey(ProbeId), Is.True);
            RemoveFromProvider();
            AssertUnregistered();
            var replacement = Register(false);
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId[ProbeId][0].trackable, Is.SameAs(replacement));
        }

        [UnityTest]
        public IEnumerator ProviderRemoval_ReleasesNeverActiveProbe_BeforeDeferredDestruction()
        {
            var probe = Register(false);
            RemoveFromProvider();
            AssertUnregistered();
            var replacement = Register(false);
            yield return null;
            Assert.That(probe == null, Is.True);
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId[ProbeId][0].trackable, Is.SameAs(replacement));
        }

        [UnityTest]
        public IEnumerator ActiveProbe_DeferredOnDestroy_DoesNotUnregisterItsReplacement()
        {
            var probe = Register(true);
            RemoveFromProvider();
            AssertUnregistered();
            var replacement = Register(false);
            yield return null;
            Assert.That(probe == null, Is.True);
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId[ProbeId][0].trackable, Is.SameAs(replacement));
        }

        [UnityTest]
        public IEnumerator ProviderRemoval_PreservesAnExplicitlyRetainedObject()
        {
            var probe = Register(true);
            probe.destroyOnRemoval = false;
            RemoveFromProvider();
            yield return null;
            Assert.That(probe != null, Is.True);
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId[ProbeId][0].trackable, Is.SameAs(probe));
            UnityEngine.Object.Destroy(probe.gameObject);
            yield return null;
            AssertUnregistered();
        }

        [UnityTest]
        public IEnumerator EnvironmentProbeManagerTeardown_UnregistersNeverActiveProbe_BeforeSpawnerReset()
        {
            UnityEngine.Object.DestroyImmediate(_manager);
            _root.SetActive(false);
            var manager = _root.AddComponent<AREnvironmentProbeManager>();
            manager.enabled = false; // Teardown is independent of starting a provider.
            _root.SetActive(true);
            var probe = CreateProbe(false);
            manager.GetType().BaseType.GetMethod("CreateTrackableFromExisting", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { probe, Data });
            UnityEngine.Object.Destroy(manager);
            yield return null;
            AssertUnregistered();
            yield return null;
            Assert.That(probe == null, Is.True);
            _manager = _root.AddComponent<FdtManualProbeTestManager>();
            _manager.UseSubsystem(_subsystem);
            Register(false);
        }

        private static XREnvironmentProbe Data => new XREnvironmentProbe(ProbeId, Vector3.one, Pose.identity,
            Vector3.one, default, TrackingState.Tracking, IntPtr.Zero);

        private AREnvironmentProbe CreateProbe(bool active)
        {
            var go = new GameObject("Removal probe");
            go.SetActive(active);
            go.transform.SetParent(_root.transform, false);
            return go.AddComponent<AREnvironmentProbe>();
        }

        private AREnvironmentProbe Register(bool active)
        {
            var probe = CreateProbe(active);
            _manager.AddExisting(probe, Data);
            RemovalProvider.AddNext = true;
            _manager.Poll();
            return probe;
        }

        private void RemoveFromProvider()
        {
            RemovalProvider.RemoveNext = true;
            _manager.Poll(); // Executes the production GetChanges, removal, events, and finally path.
        }

        private void AssertUnregistered()
        {
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId.ContainsKey(ProbeId), Is.False);
            Assert.That(_manager == null || _manager.trackables.count == 0, Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root) UnityEngine.Object.Destroy(_root);
            yield return null;
            _subsystem?.Destroy();
            _subsystem = null;
            if (_ownsSpawner) TrackableSpawner.ResetInstance();
            _ownsSpawner = false;
            RemovalProvider.RemoveNext = false;
            RemovalProvider.AddNext = false;
            _root = null;
            _manager = null;
        }

        public sealed class RemovalSubsystem : XREnvironmentProbeSubsystem { }

        public sealed class RemovalProvider : XREnvironmentProbeSubsystem.Provider
        {
            public static bool RemoveNext;
            public static bool AddNext;
            public override void Start() { }
            public override void Stop() { }
            public override void Destroy() { }
            public override TrackableChanges<XREnvironmentProbe> GetChanges(XREnvironmentProbe defaultProbe, Allocator allocator)
            {
                var changes = new TrackableChanges<XREnvironmentProbe>(AddNext ? 1 : 0, 0, RemoveNext ? 1 : 0, allocator);
                if (AddNext)
                {
                    var added = changes.added;
                    added[0] = Data;
                }
                AddNext = false;
                if (RemoveNext)
                {
                    var removed = changes.removed;
                    removed[0] = ProbeId;
                }
                RemoveNext = false;
                return changes;
            }
        }
    }
}
