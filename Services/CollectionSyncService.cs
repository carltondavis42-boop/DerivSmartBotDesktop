using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DerivSmartBotDesktop.Services
{
    public static class CollectionSyncService
    {
        public static void Sync<T, TKey>(
            ObservableCollection<T> target,
            IEnumerable<T> source,
            Func<T, TKey> keySelector,
            Action<T, T> update)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            var sourceList = source?.ToList() ?? new List<T>();
            var map = target.ToDictionary(keySelector, v => v);

            for (int i = 0; i < sourceList.Count; i++)
            {
                var incoming = sourceList[i];
                var key = keySelector(incoming);
                if (map.TryGetValue(key, out var existing))
                {
                    update(existing, incoming);
                    var currentIndex = target.IndexOf(existing);
                    if (currentIndex != i)
                        target.Move(currentIndex, i);
                }
                else
                {
                    target.Insert(i, incoming);
                }
            }

            var incomingKeys = new HashSet<TKey>(sourceList.Select(keySelector));
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!incomingKeys.Contains(keySelector(target[i])))
                    target.RemoveAt(i);
            }
        }
    }
}
