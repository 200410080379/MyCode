using System.Collections.Generic;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 泛型SyncVar - 类型安全的同步变量
    /// 参考 Mirror SyncVar 设计，支持脏标记、Hook回调
    /// 使用方式：public SyncVar<int> health = new SyncVar<int>(0);
    /// </summary>
    public class SyncVar<T>
    {
        private T value;
        private readonly Action<T, T> hook;
        private readonly int dirtyBitIndex;
        private NetworkBehaviour owner;

        /// <summary>当前值</summary>
        public T Value
        {
            get => value;
            set
            {
                if (EqualityComparer<T>.Default.Equals(this.value, value))
                    return;

                var old = this.value;
                this.value = value;

                // 设置脏标记
                SetDirty();

                // 调用Hook
                hook?.Invoke(old, value);
            }
        }

        public SyncVar(T initialValue = default, Action<T, T> hook = null, int dirtyBitIndex = -1)
        {
            this.value = initialValue;
            this.hook = hook;
            this.dirtyBitIndex = dirtyBitIndex;
        }

        /// <summary>
        /// 绑定到NetworkBehaviour（用于脏标记）
        /// </summary>
        public void Bind(NetworkBehaviour nb, int bitIndex)
        {
            owner = nb;
            // dirtyBitIndex 在编译时由代码生成器分配
        }

        private void SetDirty()
        {
            if (owner != null && dirtyBitIndex >= 0)
            {
                owner.SetDirtyBit(dirtyBitIndex);
            }
        }

        public static implicit operator T(SyncVar<T> syncVar) => syncVar.Value;

        public override string ToString() => value?.ToString() ?? "null";
    }

    /// <summary>
    /// 同步列表 - 网络同步的List
    /// 参考 Mirror SyncList 设计，支持增量操作同步
    /// </summary>
    public class SyncList<T> : IList<T>
    {
        private readonly List<T> list = new List<T>();
        private readonly Action<SyncList<T>.Operation, int, T> callback;
        private NetworkBehaviour owner;

        /// <summary>变更回调</summary>
        public event Action<Operation, int, T> OnChange;

        /// <summary>同步操作类型</summary>
        public enum Operation
        {
            OP_ADD = 0,
            OP_INSERT = 1,
            OP_REMOVEAT = 2,
            OP_SET = 3,
            OP_CLEAR = 4,
        }

        public SyncList(Action<Operation, int, T> callback = null)
        {
            this.callback = callback;
        }

        /// <summary>
        /// 绑定到NetworkBehaviour
        /// </summary>
        public void Bind(NetworkBehaviour nb)
        {
            owner = nb;
        }

        private void SetDirty()
        {
            // SyncList变更标记整个组件脏
            owner?.SetDirtyBit(0);
        }

        #region IList<T> Implementation

        public T this[int index]
        {
            get => list[index];
            set
            {
                if (index < 0 || index >= list.Count)
                    throw new System.ArgumentOutOfRangeException(nameof(index));

                var old = list[index];
                list[index] = value;
                SetDirty();
                NotifyChange(Operation.OP_SET, index, value);
            }
        }

        public int Count => list.Count;
        public bool IsReadOnly => false;

        public void Add(T item)
        {
            list.Add(item);
            SetDirty();
            NotifyChange(Operation.OP_ADD, list.Count - 1, item);
        }

        public void Insert(int index, T item)
        {
            list.Insert(index, item);
            SetDirty();
            NotifyChange(Operation.OP_INSERT, index, item);
        }

        public bool Remove(T item)
        {
            int index = list.IndexOf(item);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }
            return false;
        }

        public void RemoveAt(int index)
        {
            var item = list[index];
            list.RemoveAt(index);
            SetDirty();
            NotifyChange(Operation.OP_REMOVEAT, index, item);
        }

        public void Clear()
        {
            list.Clear();
            SetDirty();
            NotifyChange(Operation.OP_CLEAR, 0, default);
        }

        public bool Contains(T item) => list.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => list.CopyTo(array, arrayIndex);
        public int IndexOf(T item) => list.IndexOf(item);
        public IEnumerator<T> GetEnumerator() => list.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => list.GetEnumerator();

        #endregion

        /// <summary>
        /// 从网络数据重建列表（全量同步）
        /// </summary>
        public void Rebuild(T[] items)
        {
            list.Clear();
            list.AddRange(items);
        }

        /// <summary>
        /// 应用增量操作
        /// </summary>
        public void ApplyOperation(Operation op, int index, T item)
        {
            switch (op)
            {
                case Operation.OP_ADD:
                    list.Add(item);
                    break;
                case Operation.OP_INSERT:
                    list.Insert(index, item);
                    break;
                case Operation.OP_REMOVEAT:
                    if (index >= 0 && index < list.Count)
                        list.RemoveAt(index);
                    break;
                case Operation.OP_SET:
                    if (index >= 0 && index < list.Count)
                        list[index] = item;
                    break;
                case Operation.OP_CLEAR:
                    list.Clear();
                    break;
            }

            NotifyChange(op, index, item);
        }

        private void NotifyChange(Operation op, int index, T item)
        {
            callback?.Invoke(op, index, item);
            OnChange?.Invoke(op, index, item);
        }
    }

    /// <summary>同步字典（简化版）</summary>
    public class SyncDictionary<TKey, TValue> : Dictionary<TKey, TValue>
    {
        public event Action<DictOperation, TKey, TValue> OnChange;

        public enum DictOperation
        {
            OP_ADD = 0,
            OP_SET = 1,
            OP_REMOVE = 2,
            OP_CLEAR = 3,
        }

        public new void Add(TKey key, TValue value)
        {
            base.Add(key, value);
            OnChange?.Invoke(DictOperation.OP_ADD, key, value);
        }

        public new TValue this[TKey key]
        {
            get => base[key];
            set
            {
                base[key] = value;
                OnChange?.Invoke(DictOperation.OP_SET, key, value);
            }
        }

        public new bool Remove(TKey key)
        {
            TValue value = default;
            if (ContainsKey(key))
                value = base[key];

            bool result = base.Remove(key);
            if (result)
                OnChange?.Invoke(DictOperation.OP_REMOVE, key, value);
            return result;
        }

        public new void Clear()
        {
            base.Clear();
            OnChange?.Invoke(DictOperation.OP_CLEAR, default, default);
        }
    }
}
