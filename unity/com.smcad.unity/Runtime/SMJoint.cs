// SPDX-License-Identifier: MIT
using UnityEngine;

namespace SMCAD
{
    public enum SMJointType
    {
        [InspectorName("ベアリング")] Bearing,
        [InspectorName("ピストン")] Piston,
        [InspectorName("サスペンション")] Suspension,
        [InspectorName("その他")] Other,
    }

    /// <summary>
    /// The joint that connected this body to its parent body in Scrap Mechanic (information only).
    /// Joints are where the blueprint is split into bodies; posing and animation are done freely on the
    /// transforms / bones, so this component does not move anything. The GameObject's origin is the joint
    /// pivot and localAxis its axis, shown as a gizmo when selected.
    /// </summary>
    [DisallowMultipleComponent]
    public class SMJoint : MonoBehaviour
    {
        public SMJointType type = SMJointType.Bearing;
        public int jointId = -1;
        public string uuid;
        public int parentBody = -1;
        [Tooltip("ローカル座標でのジョイントの軸")]
        public Vector3 localAxis = Vector3.up;

        void OnDrawGizmosSelected()
        {
            Vector3 axis = transform.TransformDirection(localAxis.normalized);
            Gizmos.color = type == SMJointType.Bearing ? new Color(1f, 0.6f, 0.1f) : new Color(0.2f, 0.8f, 1f);
            Vector3 p = transform.position;
            Gizmos.DrawLine(p - axis * 0.4f, p + axis * 0.4f);
            Gizmos.DrawWireSphere(p, 0.06f);
        }
    }
}
