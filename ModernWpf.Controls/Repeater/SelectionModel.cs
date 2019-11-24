﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace ModernWpf.Controls
{
    internal struct SelectedItemInfo
    {
        public SelectedItemInfo(SelectionNode node, IndexPath path)
        {
            Node = new WeakReference<SelectionNode>(node);
            Path = path;
        }

        public WeakReference<SelectionNode> Node;
        public IndexPath Path;
    }

    public class SelectionModel : INotifyPropertyChanged
    {
        public SelectionModel()
        {
            // Parent is null for root node.
            m_rootNode = new SelectionNode(this, null /* parent */);
            // Parent is null for leaf node since it is shared. This is ok since we just
            // use the leaf as a placeholder and never ask stuff of it.
            m_leafNode = new SelectionNode(this, null /* parent */);
        }

        ~SelectionModel()
        {
            ClearSelection(false /*resetAnchor*/, false /*raiseSelectionChanged*/);
            m_rootNode = null;
            m_leafNode = null;
            m_selectedIndicesCached = null;
            m_selectedItemsCached = null;
        }

        public event TypedEventHandler<SelectionModel, SelectionModelSelectionChangedEventArgs> SelectionChanged;

        public event TypedEventHandler<SelectionModel, SelectionModelChildrenRequestedEventArgs> ChildrenRequested;

        public object Source
        {
            get => m_rootNode.Source;
            set
            {
                ClearSelection(true /* resetAnchor */, false /* raiseSelectionChanged */);
                m_rootNode.Source = value;
                OnSelectionChanged();
                RaisePropertyChanged("Source");
            }
        }

        public bool SingleSelect
        {
            get => m_singleSelect;
            set
            {
                if (m_singleSelect != !!value)
                {
                    m_singleSelect = value;
                    var selectedIndices = SelectedIndices;
                    if (value && selectedIndices != null && selectedIndices.Count > 0)
                    {
                        // We want to be single select, so make sure there is only 
                        // one selected item.
                        var firstSelectionIndexPath = selectedIndices[0];
                        ClearSelection(true /* resetAnchor */, false /*raiseSelectionChanged */);
                        SelectWithPathImpl(firstSelectionIndexPath, true /* select */, false /* raiseSelectionChanged */);
                        // Setting SelectedIndex will raise SelectionChanged event.
                        SelectedIndex = firstSelectionIndexPath;
                    }

                    RaisePropertyChanged("SingleSelect");
                }
            }
        }

        public IndexPath AnchorIndex
        {
            get
            {
                IndexPath anchor = null;
                if (m_rootNode.AnchorIndex>= 0)
                {
                    List<int> path = new List<int>();
                    var current = m_rootNode;
                    while (current != null && current.AnchorIndex>= 0)
                    {
                        path.Add(current.AnchorIndex);
                        current = current.GetAt(current.AnchorIndex, false);
                    }

                    anchor = new IndexPath(path);
                }

                return anchor;
            }
            set
            {
                if (value != null)
                {
                    SelectionTreeHelper.TraverseIndexPath(
                        m_rootNode,
                        value,
                        true, /* realizeChildren */
                        (currentNode, path, depth, childIndex) =>
                        {
                            currentNode.AnchorIndex = path.GetAt(depth);
                        }
                        );
                }
                else
                {
                    m_rootNode.AnchorIndex = -1;
                }

                RaisePropertyChanged("AnchorIndex");
            }
        }

        public IndexPath SelectedIndex
        {
            get
            {
                IndexPath selectedIndex = null;
                var selectedIndices = SelectedIndices;
                if (selectedIndices != null && selectedIndices.Count > 0)
                {
                    selectedIndex = selectedIndices[0];
                }

                return selectedIndex;
            }
            set
            {
                var isSelected = IsSelectedAt(value);
                if (isSelected == null || !isSelected.Value)
                {
                    ClearSelection(true /* resetAnchor */, false /*raiseSelectionChanged */);
                    SelectWithPathImpl(value, true /* select */, false /* raiseSelectionChanged */);
                    OnSelectionChanged();
                }
            }
        }

        public object SelectedItem
        {
            get
            {
                object item = null;
                var selectedItems = SelectedItems;
                if (selectedItems != null && selectedItems.Count > 0)
                {
                    item = selectedItems[0];
                }

                return item;
            }
        }

        public IReadOnlyList<object> SelectedItems
        {
            get
            {
                if (m_selectedItemsCached == null)
                {
                    List<SelectedItemInfo> selectedInfos = new List<SelectedItemInfo>();
                    if (m_rootNode.Source != null)
                    {
                        SelectionTreeHelper.Traverse(
                            m_rootNode,
                            false, /* realizeChildren */
                            (currentInfo) =>
                            {
                                if (currentInfo.Node.SelectedCount> 0)
                                {
                                    selectedInfos.Add(new SelectedItemInfo(currentInfo.Node, currentInfo.Path));
                                }
                            });
                    }

                    // Instead of creating a dumb vector that takes up the space for all the selected items,
                    // we create a custom VectorView implimentation that calls back using a delegate to find 
                    // the selected item at a particular index. This avoid having to create the storage and copying
                    // needed in a dumb vector. This also allows us to expose a tree of selected nodes into an 
                    // easier to consume flat vector view of objects.
                    var selectedItems = new SelectedItems<object>(
                        selectedInfos,
                        (infos, index) => // callback for GetAt(index)
                        {
                            int currentIndex = 0;
                            object item = null;
                            foreach (var info in infos)
                            {
                                if (info.Node.TryGetTarget(out var node))
                                {
                                    int currentCount = node.SelectedCount;
                                    if (index >= currentIndex && index < currentIndex + currentCount)
                                    {
                                        int targetIndex = node.SelectedIndices()[index - currentIndex];
                                        item = node.ItemsSourceView.GetAt(targetIndex);
                                        break;
                                    }

                                    currentIndex += currentCount;
                                }
                                else
                                {
                                    throw new Exception("selection has changed since SelectedItems property was read.");
                                }
                            }

                            return item;
                        });
                    m_selectedItemsCached = selectedItems;
                }

                return m_selectedItemsCached;
            }
        }

        public IReadOnlyList<IndexPath> SelectedIndices
        {
            get
            {
                if (m_selectedIndicesCached == null)
                {
                    List<SelectedItemInfo> selectedInfos = new List<SelectedItemInfo>();
                    SelectionTreeHelper.Traverse(
                        m_rootNode,
                        false, /* realizeChildren */
                        (currentInfo) =>
                        {
                            if (currentInfo.Node.SelectedCount> 0)
                            {
                                selectedInfos.Add(new SelectedItemInfo(currentInfo.Node, currentInfo.Path));
                            }
                        });

                    // Instead of creating a dumb vector that takes up the space for all the selected indices,
                    // we create a custom VectorView implimentation that calls back using a delegate to find 
                    // the IndexPath at a particular index. This avoid having to create the storage and copying
                    // needed in a dumb vector. This also allows us to expose a tree of selected nodes into an 
                    // easier to consume flat vector view of IndexPaths.
                    var indices = new SelectedItems<IndexPath>(
                        selectedInfos,
                        (infos, index) => // callback for GetAt(index)
                        {
                            int currentIndex = 0;
                            IndexPath path = null;
                            foreach (var info in infos)
                            {
                                if (info.Node.TryGetTarget(out var node))
                                {
                                    int currentCount = node.SelectedCount;
                                    if (index >= currentIndex && index < currentIndex + currentCount)
                                    {
                                        int targetIndex = node.SelectedIndices()[index - currentIndex];
                                        path = (info.Path).CloneWithChildIndex(targetIndex);
                                        break;
                                    }

                                    currentIndex += currentCount;
                                }
                                else
                                {
                                    throw new Exception("selection has changed since SelectedIndices property was read.");
                                }
                            }

                            return path;
                        });
                    m_selectedIndicesCached = indices;
                }

                return m_selectedIndicesCached;
            }
        }

        public void SetAnchorIndex(int index)
        {
            AnchorIndex = new IndexPath(index);
        }

        public void SetAnchorIndex(int groupIndex, int itemIndex)
        {
            AnchorIndex = new IndexPath(groupIndex, itemIndex);
        }

        public void Select(int index)
        {
            SelectImpl(index, true /* select */);
        }

        public void Select(int groupIndex, int itemIndex)
        {
            SelectWithGroupImpl(groupIndex, itemIndex, true /* select */);
        }

        public void SelectAt(IndexPath index)
        {
            SelectWithPathImpl(index, true /* select */, true /* raiseSelectionChanged */);
        }

        public void Deselect(int index)
        {
            SelectImpl(index, false /* select */);
        }

        public void Deselect(int groupIndex, int itemIndex)
        {
            SelectWithGroupImpl(groupIndex, itemIndex, false /* select */);
        }

        public void DeselectAt(IndexPath index)
        {
            SelectWithPathImpl(index, false /* select */, true /* raiseSelectionChanged */);
        }

        public bool? IsSelected(int index)
        {
            Debug.Assert(index >= 0);
            var isSelected = m_rootNode.IsSelectedWithPartial(index);
            return isSelected;
        }

        public bool? IsSelected(int groupIndex, int itemIndex)
        {
            Debug.Assert(groupIndex >= 0 && itemIndex >= 0);
            bool? isSelected = false;
            var childNode = m_rootNode.GetAt(groupIndex, false /*realizeChild*/);
            if (childNode != null)
            {
                isSelected = childNode.IsSelectedWithPartial(itemIndex);
            }

            return isSelected;
        }

        public bool? IsSelectedAt(IndexPath index)
        {
            var path = index;
            Debug.Assert(path.IsValid());
            bool isRealized = true;
            var node = m_rootNode;
            for (int i = 0; i < path.GetSize() - 1; i++)
            {
                var childIndex = path.GetAt(i);
                node = node.GetAt(childIndex, false /* realizeChild */);
                if (node == null)
                {
                    isRealized = false;
                    break;
                }
            }

            bool? isSelected = false;
            if (isRealized)
            {
                var size = path.GetSize();
                if (size == 0)
                {
                    isSelected = SelectionNode.ConvertToNullableBool(node.EvaluateIsSelectedBasedOnChildrenNodes());
                }
                else
                {
                    isSelected = node.IsSelectedWithPartial(path.GetAt(size - 1));
                }
            }

            return isSelected;
        }

        public void SelectRangeFromAnchor(int index)
        {
            SelectRangeFromAnchorImpl(index, true /* select */ );
        }

        public void SelectRangeFromAnchor(int groupIndex, int itemIndex)
        {
            SelectRangeFromAnchorWithGroupImpl(groupIndex, itemIndex, true /* select */);
        }

        public void SelectRangeFromAnchorTo(IndexPath index)
        {
            SelectRangeImpl(AnchorIndex, index, true /* select */);
        }

        public void DeselectRangeFromAnchor(int index)
        {
            SelectRangeFromAnchorImpl(index, false /* select */);
        }

        public void DeselectRangeFromAnchor(int groupIndex, int itemIndex)
        {
            SelectRangeFromAnchorWithGroupImpl(groupIndex, itemIndex, false /* select */);
        }

        public void DeselectRangeFromAnchorTo(IndexPath index)
        {
            SelectRangeImpl(AnchorIndex, index, false /* select */);
        }

        public void SelectRange(IndexPath start, IndexPath end)
        {
            SelectRangeImpl(start, end, true /* select */);
        }

        public void DeselectRange(IndexPath start, IndexPath end)
        {
            SelectRangeImpl(start, end, false /* select */);
        }

        public void SelectAll()
        {
            SelectionTreeHelper.Traverse(
                m_rootNode,
                true, /* realizeChildren */
                info =>
                {
                    if (info.Node.DataCount> 0)
                    {
                        info.Node.SelectAll();
                    }
                });

            OnSelectionChanged();
        }

        public void ClearSelection()
        {
            ClearSelection(true /*resetAnchor*/, true /* raiseSelectionChanged */);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            RaisePropertyChanged(propertyName);
        }

        void RaisePropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        internal void OnSelectionInvalidatedDueToCollectionChange()
        {
            OnSelectionChanged();
        }

        internal SelectionNode SharedLeafNode() { return m_leafNode; }

        internal object ResolvePath(object data, WeakReference<SelectionNode> sourceNode)
        {
            object resolved = null;
            // Raise ChildrenRequested event if there is a handler
            var childrenRequested = ChildrenRequested;
            if (childrenRequested != null)
            {
                if (m_childrenRequestedEventArgs == null)
                {
                    m_childrenRequestedEventArgs = new SelectionModelChildrenRequestedEventArgs(data, sourceNode);
                }
                else
                {
                    m_childrenRequestedEventArgs.Initialize(data, sourceNode);
                }


                childrenRequested(this, m_childrenRequestedEventArgs);
                resolved = m_childrenRequestedEventArgs.Children;

                // Clear out the values in the args so that it cannot be used after the event handler call.
                m_childrenRequestedEventArgs.Initialize(null, new WeakReference<SelectionNode>(null) /* empty weakptr */);
            }
            else
            {
                // No handlers for ChildrenRequested event. If data is of type ItemsSourceView
                // or a type that can be used to create a ItemsSourceView using ItemsSourceView.CreateFrom, then we can
                // auto-resolve that as the child. If not, then we consider the value as a leaf. This is to 
                // avoid having to provide the event handler for the most common scenarios. If the app dev does
                // not want this default behavior, they can provide the handler to override.
                if (data is ItemsSourceView ||
                    data is IList ||
                    data is IEnumerable)
                {
                    resolved = data;
                }
            }

            return resolved;
        }

        void ClearSelection(bool resetAnchor, bool raiseSelectionChanged)
        {
            SelectionTreeHelper.Traverse(
            m_rootNode,
            false, /* realizeChildren */
            info =>
            {
                info.Node.Clear();
            });

            if (resetAnchor)
            {
                AnchorIndex = null;
            }

            if (raiseSelectionChanged)
            {
                OnSelectionChanged();
            }
        }

        void OnSelectionChanged()
        {
            m_selectedIndicesCached = null;
            m_selectedItemsCached = null;

            // Raise SelectionChanged event
            var selectionChanged = SelectionChanged;
            if (selectionChanged != null)
            {
                if (m_selectionChangedEventArgs == null)
                {
                    m_selectionChangedEventArgs = new SelectionModelSelectionChangedEventArgs();
                }

                selectionChanged(this, m_selectionChangedEventArgs);
            }

            RaisePropertyChanged("SelectedIndex");
            RaisePropertyChanged("SelectedIndices");
            if (m_rootNode.Source != null)
            {
                RaisePropertyChanged("SelectedItem");
                RaisePropertyChanged("SelectedItems");
            }
        }

        void SelectImpl(int index, bool select)
        {
            if (m_singleSelect)
            {
                ClearSelection(true /*resetAnchor*/, false /* raiseSelectionChanged */);
            }

            var selected = m_rootNode.Select(index, select);
            if (selected)
            {
                AnchorIndex = new IndexPath(index);
            }

            OnSelectionChanged();
        }

        void SelectWithGroupImpl(int groupIndex, int itemIndex, bool select)
        {
            if (m_singleSelect)
            {
                ClearSelection(true /*resetAnchor*/, false /* raiseSelectionChanged */);
            }

            var childNode = m_rootNode.GetAt(groupIndex, true /* realize */);
            var selected = childNode.Select(itemIndex, select);
            if (selected)
            {
                AnchorIndex = new IndexPath(groupIndex, itemIndex);
            }

            OnSelectionChanged();
        }

        void SelectWithPathImpl(IndexPath index, bool select, bool raiseSelectionChanged)
        {
            bool selected = false;
            if (m_singleSelect)
            {
                ClearSelection(true /*restAnchor*/, false /* raiseSelectionChanged */);
            }

            SelectionTreeHelper.TraverseIndexPath(
                m_rootNode,
                index,
                true, /* realizeChildren */

                (currentNode, path, depth, childIndex) =>
                {
                    if (depth == path.GetSize() - 1)
                    {
                        selected = currentNode.Select(childIndex, select);
                    }
                }
            );

            if (selected)
            {
                AnchorIndex = index;
            }

            if (raiseSelectionChanged)
            {
                OnSelectionChanged();
            }
        }

        void SelectRangeFromAnchorImpl(int index, bool select)
        {
            int anchorIndex = 0;
            var anchor = AnchorIndex;
            if (anchor != null)
            {
                Debug.Assert(anchor.GetSize() == 1);
                anchorIndex = anchor.GetAt(0);
            }

            bool selected = m_rootNode.SelectRange(new IndexRange(anchorIndex, index), select);
            if (selected)
            {
                OnSelectionChanged();
            }
        }

        void SelectRangeFromAnchorWithGroupImpl(int endGroupIndex, int endItemIndex, bool select)
        {
            int startGroupIndex = 0;
            int startItemIndex = 0;
            var anchorIndex = AnchorIndex;
            if (anchorIndex != null)
            {
                Debug.Assert(anchorIndex.GetSize() == 2);
                startGroupIndex = anchorIndex.GetAt(0);
                startItemIndex = anchorIndex.GetAt(1);
            }

            // Make sure start > end
            if (startGroupIndex > endGroupIndex ||
                (startGroupIndex == endGroupIndex && startItemIndex > endItemIndex))
            {
                int temp = startGroupIndex;
                startGroupIndex = endGroupIndex;
                endGroupIndex = temp;
                temp = startItemIndex;
                startItemIndex = endItemIndex;
                endItemIndex = temp;
            }

            bool selected = false;
            for (int groupIdx = startGroupIndex; groupIdx <= endGroupIndex; groupIdx++)
            {
                var groupNode = m_rootNode.GetAt(groupIdx, true /* realizeChild */);
                int startIndex = groupIdx == startGroupIndex ? startItemIndex : 0;
                int endIndex = groupIdx == endGroupIndex ? endItemIndex : groupNode.DataCount- 1;
                selected |= groupNode.SelectRange(new IndexRange(startIndex, endIndex), select);
            }

            if (selected)
            {
                OnSelectionChanged();
            }
        }

        void SelectRangeImpl(IndexPath start, IndexPath end, bool select)
        {
            var winrtStart = start;
            var winrtEnd = end;

            // Make sure start <= end 
            if (winrtEnd.CompareTo(winrtStart) == -1)
            {
                var temp = winrtStart;
                winrtStart = winrtEnd;
                winrtEnd = temp;
            }

            // Note: Since we do not know the depth of the tree, we have to walk to each leaf
            SelectionTreeHelper.TraverseRangeRealizeChildren(
                m_rootNode,
                winrtStart,
                winrtEnd,

                info =>
                {
                    if (info.Node.DataCount== 0)
                    {
                        // Select only leaf nodes
                        info.ParentNode.Select(info.Path.GetAt(info.Path.GetSize() - 1), select);
                    }
                });

            OnSelectionChanged();
        }

        SelectionNode m_rootNode;
        bool m_singleSelect = false;

        IReadOnlyList<IndexPath> m_selectedIndicesCached;
        IReadOnlyList<object> m_selectedItemsCached;

        // Cached Event args to avoid creation cost every time
        SelectionModelChildrenRequestedEventArgs m_childrenRequestedEventArgs;
        SelectionModelSelectionChangedEventArgs m_selectionChangedEventArgs;

        // use just one instance of a leaf node to avoid creating a bunch of these.
        SelectionNode m_leafNode;
    }
}
