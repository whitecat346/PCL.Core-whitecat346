using PCL.Core.Link.Scaffolding.Client.Models;
using System.Collections.Generic;

namespace PCL.Core.Link.Scaffolding;

public static class PlayerListHandler
{
    /// <summary>
    /// Sort the player list so that the host is always first.
    /// </summary>
    /// <param name="list">Original player list.</param>
    /// <returns>Sorted player list.</returns>
    public static List<PlayerProfile> Sort(IReadOnlyList<PlayerProfile> list)
    {
        var sorted = new List<PlayerProfile>();
        foreach (var profile in list)
        {
            if (profile.Kind == PlayerKind.HOST)
            {
                sorted.Insert(0, profile);
            }
            else
            {
                sorted.Add(profile);
            }
        }
        return sorted;
    }
}