using System.Collections.Specialized;
using System.ComponentModel;
using Encodex.Models;
using Xunit;

namespace Encodex.Tests.Models;

public class RangeObservableCollectionTests
{
    [Fact]
    public void AddRange_RaisesSingleResetNotification()
    {
        var collection = new RangeObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => notifications.Add(e);

        collection.AddRange(new[] { 1, 2, 3, 4, 5 });

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, collection);
        // Filling a large scan result item by item would raise one event per row;
        // the bulk add must cost exactly one Reset.
        var args = Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, args.Action);
    }

    [Fact]
    public void AddRange_RaisesCountAndIndexerPropertyChanged()
    {
        var collection = new RangeObservableCollection<int>();
        var properties = new List<string?>();
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        collection.AddRange(new[] { 1, 2 });

        Assert.Contains(nameof(collection.Count), properties);
        Assert.Contains("Item[]", properties);
    }

    [Fact]
    public void AddRange_Null_Throws()
    {
        var collection = new RangeObservableCollection<int>();

        Assert.Throws<ArgumentNullException>(() => collection.AddRange(null!));
    }

    [Fact]
    public void AddRange_Empty_KeepsCollectionEmpty()
    {
        var collection = new RangeObservableCollection<int>();

        collection.AddRange(Array.Empty<int>());

        Assert.Empty(collection);
    }

    [Fact]
    public void Add_SingleItem_StillRaisesPerItemNotification()
    {
        // The suppression only applies during AddRange; ordinary Adds keep their
        // fine-grained events so the UI stays in sync for single insertions.
        var collection = new RangeObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => notifications.Add(e);

        collection.Add(42);

        var args = Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Add, args.Action);
    }
}
