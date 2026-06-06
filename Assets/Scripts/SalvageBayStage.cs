using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/*
    Load the in-game bay, to find the right place to put the ship
*/
public class SalvageBayStage : MonoBehaviour
{
    public bool reload = false;

    GameObject bay;
    GameObject jack;
    GameObject visibleBay;
    GameObject visibleJack;

    void OnValidate()
    {
        if(reload)
        {
            reload = false;
            LoadBay();
        }
    }

    void LoadBay()
    {
        if(bay == null || jack == null)
        {
            LoadAssetByGuid("4e97499d6abdd314a83597595300db8d", go => { bay = go; if (bay != null && jack != null) RenderBay(); });
            LoadAssetByGuid("9d2adb8555f4e794d874d4c67f643547", go => { jack = go; if (bay != null && jack != null) RenderBay(); });
        }
        else
        {
            RenderBay();
        }
    }

    void LoadAssetByGuid(string guid, System.Action<GameObject> onLoaded)
    {
        var locOp = Addressables.LoadResourceLocationsAsync(guid, typeof(GameObject));
        locOp.Completed += locHandle => {
            if (locHandle.Result == null || locHandle.Result.Count == 0) { Debug.LogWarning($"SalvageBayStage: no locations for {guid}"); return; }
            var loadOp = Addressables.LoadAssetAsync<GameObject>(locHandle.Result[0]);
            loadOp.Completed += h => { if (h.Result != null) onLoaded(h.Result); else Debug.LogWarning($"SalvageBayStage: failed to load {guid}"); };
        };
    }

    void RenderBay()
    {
        visibleBay = GameObject.Instantiate(bay, new Vector3(21.83f, 11.8f, 35.9f), Quaternion.identity, transform);
        visibleJack = GameObject.Instantiate(jack, new Vector3(22.35f, 9.5f, 112.9f), Quaternion.Euler(0, -90, 0), transform);
    }
}
