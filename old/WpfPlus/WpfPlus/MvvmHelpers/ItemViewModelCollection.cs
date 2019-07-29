using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;

namespace WpfPlus.MvvmHelpers
{
    // Inspired by http://stackoverflow.com/questions/15830008/mvvm-and-collections-of-vms
    public class ItemViewModelCollection<TItemViewModel, TItemModel> : ObservableCollection<TItemViewModel>
        where TItemViewModel : class, IItemViewModel<TItemModel> where TItemModel : class
    {
        public delegate TItemViewModel InstantiateItemViewModelFunc(TItemModel model);

        private bool _syncEnabled = true;

        private readonly ICollection<TItemModel> _itemModelCollection;
        private readonly InstantiateItemViewModelFunc _instantiateItemViewModelFunc;

        public ItemViewModelCollection(ICollection<TItemModel> itemModelCollection, InstantiateItemViewModelFunc instantiateItemViewModelFunc)
        {
            _itemModelCollection = itemModelCollection;
            _instantiateItemViewModelFunc = instantiateItemViewModelFunc;

            foreach (TItemModel itemModel in _itemModelCollection)
                AddItemViewModelForItemModel(itemModel);

            var observableItemModelCollection = _itemModelCollection as ObservableCollection<TItemModel>;
            if (observableItemModelCollection != null)
                observableItemModelCollection.CollectionChanged += ItemModelCollectionChanged;

            CollectionChanged += ItemViewModelCollectionChanged;
        }

        public TItemViewModel GetItemViewModelByItemModel(TItemModel itemModel) => Items.FirstOrDefault(vm => vm.BaseModel == itemModel);

        public sealed override event NotifyCollectionChangedEventHandler CollectionChanged
        {
            add { base.CollectionChanged += value; }
            remove { base.CollectionChanged -= value; }
        }

        private void AddItemViewModelForItemModel(TItemModel itemModel)
        {
            if (itemModel == null)
                return;

            Add(_instantiateItemViewModelFunc(itemModel));
        }

        private void RemoveItemViewModelByItemModel(TItemModel itemModel)
        {
            if (itemModel == null)
                return;

            foreach (TItemViewModel itemViewModel in Items.Where(vm => vm.BaseModel == itemModel))
                Remove(itemViewModel);
        }

        private void ItemModelCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_syncEnabled)
                return;

            _syncEnabled = false;
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        foreach (TItemModel itemModel in e.NewItems)
                            AddItemViewModelForItemModel(itemModel);
                        break;

                    case NotifyCollectionChangedAction.Remove:
                        foreach (TItemModel itemModel in e.OldItems)
                            RemoveItemViewModelByItemModel(itemModel);
                        break;

                    case NotifyCollectionChangedAction.Reset:
                        Clear();
                        foreach (TItemModel itemModel in e.NewItems)
                            AddItemViewModelForItemModel(itemModel);
                        break;
                }
            }
            _syncEnabled = true;
        }

        private void ItemViewModelCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_syncEnabled)
                return;

            _syncEnabled = false;
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        foreach (TItemViewModel itemViewModel in e.NewItems)
                            _itemModelCollection.Add(itemViewModel.BaseModel);
                        break;

                    case NotifyCollectionChangedAction.Remove:
                        foreach (TItemViewModel itemViewModel in e.OldItems)
                            _itemModelCollection.Remove(itemViewModel.BaseModel);
                        break;

                    case NotifyCollectionChangedAction.Reset:
                        _itemModelCollection.Clear();
                        foreach (TItemViewModel itemViewModel in e.NewItems)
                            _itemModelCollection.Add(itemViewModel.BaseModel);
                        break;
                }
            }
            _syncEnabled = true;
        }
    }
}