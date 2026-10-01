using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Lets the generated-ship validators look at one scene only, so they can run next to other loaded scenes (the unified project validator keeps the
/// Ship scene open). Physics and NavMesh queries are global, so they only run when nothing else is loaded.
/// </summary>
public static class SceneScope
{
    /// <summary>The scene to look at; invalid means every loaded scene (the normal case when the generated ship is open alone).</summary>
    public static Scene Current;

    public static bool PhysicsSafe => !Current.IsValid() || SceneManager.sceneCount == 1;

    public static T[] All<T>() where T : Object
    {
        var all = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
        if (!Current.IsValid()) return all;
        return all.Where(o => !(o is Component c) || c.gameObject.scene == Current).ToArray();
    }

    public static T First<T>() where T : Object => All<T>().FirstOrDefault();
}
