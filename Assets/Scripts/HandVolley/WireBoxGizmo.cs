using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 히트박스를 씬 뷰 와이어프레임으로만 표시한다.
    /// 반투명 머티리얼은 렌더 파이프라인 설정에 따라 불투명하게 나와
    /// 손이 네모난 상자로 보이는 원인이 되므로, 게임 화면에는 그리지 않는다.
    /// </summary>
    public class WireBoxGizmo : MonoBehaviour
    {
        [SerializeField] private Color _color = new Color(0.4f, 0.9f, 1f, 0.7f);

        private void OnDrawGizmos()
        {
            Gizmos.color = _color;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }
    }
}
