using System;
using System.Collections.Generic;
using System.Linq;

namespace Pi_Hosts {
    internal class TreeNode<T> : IComparable<TreeNode<T>> where T : IComparable<T> {
        public SortedSet<TreeNode<T>> Children { get; } = new SortedSet<TreeNode<T>>();

        public T Item { get; private set; }

        public TreeNode<T> Parent { get; private set; } = null;

        public bool HasChildren => Children.Count > 0;

        public TreeNode<T> GetChildWith(T item) {
            lock (Children) return Children.FirstOrDefault(x => x.Item.Equals(item));
        }

        public TreeNode(T item) => Item = item;

        public TreeNode<T> AddChild(T item) {
            var existingChild = GetChildWith(item);
            if (existingChild == null) {
                TreeNode<T> nodeItem = new TreeNode<T>(item);
                nodeItem.Parent = this;
                lock (Children)
                    Children.Add(nodeItem);
                return nodeItem;
            }
            return existingChild;
        }

        public void Print(string indent = "", bool last = true) {
            Console.Write(indent);
            if (last) {
                Console.Write("\\-");
                indent += "  ";
            } else {
                Console.Write("|-");
                indent += "| ";
            }
            Console.WriteLine(Item);
            
            for (int i = 0; i < Children.Count; ++i) {
                Children.ElementAt(i).Print(indent, i == Children.Count - 1);
            }
        }

        public void ForEachItem(Action<T> a) {
            a(Item);

            for (int i = 0; i < Children.Count; ++i) {
                Children.ElementAt(i).ForEachItem(a);
            }
        }

        public void ForEachNode(Action<TreeNode<T>> a) {
            a(this);

            for (int i = 0; i < Children.Count; ++i) {
                Children.ElementAt(i).ForEachNode(a);
            }
        }

        public int CompareTo(TreeNode<T> other) => this.Item.CompareTo(other.Item);
    }
}
