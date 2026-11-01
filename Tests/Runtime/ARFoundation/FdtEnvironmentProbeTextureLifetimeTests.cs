// FDT patch: inactive initialization and explicit ownership of external Cubemap wrappers.
using System;
using System.Collections;
using NUnit.Framework;
using Unity.XR.CoreUtils;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.XR.ARSubsystems;

namespace UnityEngine.XR.ARFoundation.Tests
{
    public sealed class FdtEnvironmentProbeTextureLifetimeTests
    {
        private GameObject _root;
        private Cubemap _source;
        private Cubemap _foreign;
        private FdtManualProbeTestManager _manager;
        private bool _ownsSpawner;
        private static readonly TrackableId ProbeId = new TrackableId(88, 1);

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("External Cubemap wrapper lifetime requires a graphics device.");
            Assert.That(UnityEngine.Object.FindAnyObjectByType<XROrigin>(), Is.Null, "The fixture must not replace another rig's spawner.");
            _root = new GameObject("Probe texture lifetime fixture");
            var origin = _root.AddComponent<XROrigin>();
            TrackableSpawner.instance.SetOrigin(origin);
            _ownsSpawner = true;
            _manager = _root.AddComponent<FdtManualProbeTestManager>();
            _source = new Cubemap(4, TextureFormat.RGBA32, false);
            _source.Apply();
            _foreign = new Cubemap(2, TextureFormat.RGBA32, false);
            _foreign.Apply();
            yield return null;
        }

        [UnityTest]
        public IEnumerator InactiveProbe_InitializesOnActivation_ReusesItsWrapper_AndDestroysOnlyOwnedTexture()
        {
            var probe = CreateInactiveProbe();
            var reflection = probe.GetComponent<ReflectionProbe>();
            reflection.customBakedTexture = _foreign;
            Register(probe);
            Assert.That(probe.gameObject.activeSelf, Is.False);
            Assert.That(probe.sessionRelativeData.textureDescriptor.valid, Is.True);
            Assert.That(probe.textureDescriptor.valid, Is.False, "A never-active object has no owned wrapper to retire.");
            Assert.That(reflection.size, Is.EqualTo(new Vector3(2f, 3f, 4f)));
            probe.gameObject.SetActive(true);
            Assert.That(probe.textureDescriptor.valid, Is.True, "Activation consumes the retained data without a provider update.");
            var owned = reflection.customBakedTexture;
            Assert.That(owned, Is.Not.Null);
            Assert.That(owned, Is.Not.SameAs(_source));
            Assert.That(owned, Is.Not.SameAs(_foreign));
            reflection.customBakedTexture = null;
            probe.OnAfterSetSessionRelativeData();
            Assert.That(reflection.customBakedTexture, Is.SameAs(owned), "Clearing an assignment must not create a second wrapper.");
            reflection.customBakedTexture = _foreign;
            UnityEngine.Object.Destroy(probe.gameObject);
            // GameObject destruction disposes the wrapper; its Texture destruction is also deferred.
            yield return null;
            yield return null;
            Assert.That(owned == null, Is.True, "The owned Unity texture is released without UnloadUnusedAssets.");
            Assert.That(_source != null, Is.True, "The provider's Unity texture object remains alive.");
            Assert.That(_foreign != null, Is.True, "An externally assigned texture is borrowed.");
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId.ContainsKey(ProbeId), Is.False,
                "Base OnDestroy must unregister the trackable.");
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator NeverActivatedProbe_DoesNotCreateAnOwnedWrapper()
        {
            var probe = CreateInactiveProbe();
            Register(probe);
            Assert.That(probe.GetComponent<ReflectionProbe>().customBakedTexture, Is.Null);
            Assert.That(probe.textureDescriptor.valid, Is.False);
            Assert.That(_manager.RemovePending(ProbeId), Is.True);
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId.ContainsKey(ProbeId), Is.False,
                "Removal must release the registration before ResetInstance or OnDestroy.");
            var replacement = CreateInactiveProbe();
            Register(replacement);
            yield return null;
            Assert.That(probe == null, Is.True);
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId[ProbeId][0].trackable,
                Is.SameAs(replacement), "Reusing an ID must retain the replacement registration.");
            Assert.That(_source != null, Is.True);
            Assert.That(_manager.RemovePending(ProbeId), Is.True);
            Assert.That(TrackableSpawner.instance.m_EntriesByTrackableId.ContainsKey(ProbeId), Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        private AREnvironmentProbe CreateInactiveProbe()
        {
            var go = new GameObject("Inactive probe");
            go.SetActive(false);
            go.transform.SetParent(_root.transform, false);
            return go.AddComponent<AREnvironmentProbe>();
        }

        private void Register(AREnvironmentProbe probe)
        {
            var descriptor = new XRTextureDescriptor(_source.GetNativeTexturePtr(), _source.width, _source.height,
                _source.mipmapCount, _source.format, 0, 1, XRTextureType.Cube);
            var data = new XREnvironmentProbe(ProbeId, Vector3.one, Pose.identity, new Vector3(2f, 3f, 4f),
                descriptor, TrackingState.Tracking, IntPtr.Zero);
            _manager.AddExisting(probe, data);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root) UnityEngine.Object.Destroy(_root);
            if (_source) UnityEngine.Object.Destroy(_source);
            if (_foreign) UnityEngine.Object.Destroy(_foreign);
            yield return null;
            if (_ownsSpawner) TrackableSpawner.ResetInstance();
            _ownsSpawner = false;
            _root = null;
            _source = null;
            _foreign = null;
            _manager = null;
        }
    }
}
