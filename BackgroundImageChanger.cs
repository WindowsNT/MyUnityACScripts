using UnityEngine;
using AC;

public class ACBackgroundSwitcher : MonoBehaviour
{
    public Texture2D dayTexture;
    public Texture2D nightTexture;

    private BackgroundImage bgImage;
    private int lastVal;

    void Awake()
    {
        bgImage = GetComponent<BackgroundImage>();
    }

    void Update()
    {
        int Time = GlobalVariables.GetIntegerValue(3);
        if (Time != lastVal)
        {
            lastVal = Time;
            var tex = Time == 1 ? nightTexture : dayTexture;
            bgImage.backgroundTexture = tex;
            BackgroundImageUI.Instance.SetTexture(tex);  // αυτό κάνει το actual update
        }
    }
}