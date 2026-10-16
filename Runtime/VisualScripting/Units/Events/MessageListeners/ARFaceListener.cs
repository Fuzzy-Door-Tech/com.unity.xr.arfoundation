#if VISUALSCRIPTING_1_8_OR_NEWER

using System;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif
using Unity.VisualScripting;

namespace UnityEngine.XR.ARFoundation.VisualScripting
{
    /// <summary>
    /// Listens to the <see cref="ARFace.updated"/> event and forwards it to the visual scripting event bus.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class ARFaceListener : MessageListener
    {
        ARFace m_Face;

#if UNITY_6000_5_OR_NEWER
        [NoAutoStaticsCleanup]
#endif
        static readonly Type k_HookNameKey = typeof(ARFaceUpdatedEventArgs);

        void OnFaceUpdated(ARFaceUpdatedEventArgs args)
            => EventBus.Trigger(Constants.EventHookNames[k_HookNameKey], gameObject, args);

        void OnEnable()
        {
            m_Face = GetComponent<ARFace>();
            if (m_Face == null)
                throw new InvalidOperationException(
                    $"{nameof(ARFaceListener)} requires an active and enabled {nameof(ARFace)} component on"
                    + $"{name} GameObject, but none was found.");

            m_Face.updated += OnFaceUpdated;
        }

        void OnDisable()
            => m_Face.updated -= OnFaceUpdated;
    }
}

#endif // VISUALSCRIPTING_1_8_OR_NEWER
