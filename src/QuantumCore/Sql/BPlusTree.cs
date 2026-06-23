// ============================================================================
// 量子核（QuantumCore）— B+Tree 索引
// 用于范围查询和排序，支持等值查找和范围扫描
// ============================================================================

namespace QuantumCore.Sql;

/// <summary>
/// B+Tree 节点
/// </summary>
internal sealed class BPlusTreeNode
{
    public bool IsLeaf { get; }
    public List<object> Keys { get; } = new();
    public List<BPlusTreeNode> Children { get; } = new();   // 内部节点
    public List<long> Offsets { get; } = new();              // 叶子节点：行偏移量
    public BPlusTreeNode? Next { get; set; }                  // 叶子节点：指向下一个叶子

    public BPlusTreeNode(bool isLeaf)
    {
        IsLeaf = isLeaf;
    }
}

/// <summary>
/// B+Tree 索引 — 支持等值查找、范围查询、排序
/// </summary>
internal sealed class BPlusTree
{
    private readonly int _order;          // 阶数（每个节点最大子节点数）
    private BPlusTreeNode _root;

    public BPlusTree(int order = 64)
    {
        _order = order;
        _root = new BPlusTreeNode(isLeaf: true);
    }

    /// <summary>
    /// 插入一个键和对应的行偏移量
    /// </summary>
    public void Insert(object key, long offset)
    {
        var result = InsertRec(_root, key, offset);
        if (result.HasValue)
        {
            var (node, upKey) = result.Value;
            // 根节点分裂，创建新的根
            var newRoot = new BPlusTreeNode(isLeaf: false);
            newRoot.Keys.Add(upKey);
            newRoot.Children.Add(_root);
            newRoot.Children.Add(node);
            _root = newRoot;
        }
    }

    /// <summary>
    /// 等值查找：返回匹配的行偏移量列表
    /// </summary>
    public List<long> Search(object key)
    {
        var leaf = FindLeaf(key);
        var results = new List<long>();

        for (int i = 0; i < leaf.Keys.Count; i++)
        {
            if (CompareKeys(leaf.Keys[i], key) == 0)
                results.Add(leaf.Offsets[i]);
        }

        return results;
    }

    /// <summary>
    /// 范围查询：返回 [start, end) 范围内的行偏移量
    /// </summary>
    public List<long> RangeSearch(object? start, object? end)
    {
        var results = new List<long>();
        var leaf = start != null ? FindLeaf(start) : GetLeftmostLeaf();

        while (leaf != null)
        {
            for (int i = 0; i < leaf.Keys.Count; i++)
            {
                var key = leaf.Keys[i];

                if (start != null && CompareKeys(key, start) < 0) continue;
                if (end != null && CompareKeys(key, end) >= 0) return results;

                results.Add(leaf.Offsets[i]);
            }
            leaf = leaf.Next;
        }

        return results;
    }

    /// <summary>
    /// 获取所有行偏移量（用于全表扫描）
    /// </summary>
    public List<long> GetAll()
    {
        var results = new List<long>();
        var leaf = GetLeftmostLeaf();

        while (leaf != null)
        {
            results.AddRange(leaf.Offsets);
            leaf = leaf.Next;
        }

        return results;
    }

    public int Count { get; private set; }

    // ── 内部实现 ──

    private BPlusTreeNode FindLeaf(object key)
    {
        var current = _root;
        while (!current.IsLeaf)
        {
            int i = 0;
            while (i < current.Keys.Count && CompareKeys(key, current.Keys[i]) >= 0)
                i++;
            current = current.Children[i];
        }
        return current;
    }

    private BPlusTreeNode GetLeftmostLeaf()
    {
        var current = _root;
        while (!current.IsLeaf)
            current = current.Children[0];
        return current;
    }

    private (BPlusTreeNode Node, object Key)? InsertRec(
        BPlusTreeNode node, object key, long offset)
    {
        if (node.IsLeaf)
        {
            // 找到插入位置
            int i = 0;
            while (i < node.Keys.Count && CompareKeys(key, node.Keys[i]) > 0)
                i++;

            node.Keys.Insert(i, key);
            node.Offsets.Insert(i, offset);
            Count++;

            // 叶子节点分裂
            if (node.Keys.Count >= _order)
                return SplitLeaf(node);

            return null;
        }

        // 内部节点：递归插入
        int childIdx = 0;
        while (childIdx < node.Keys.Count && CompareKeys(key, node.Keys[childIdx]) >= 0)
            childIdx++;

        var result = InsertRec(node.Children[childIdx], key, offset);
        if (result == null) return null;

        // 插入分裂后的键和节点
        var (childNode, childKey) = result.Value;
        node.Keys.Insert(childIdx, childKey);
        node.Children.Insert(childIdx + 1, childNode);

        // 内部节点分裂
        if (node.Keys.Count >= _order)
            return SplitInternal(node);

        return null;
    }

    private (BPlusTreeNode Node, object Key) SplitLeaf(BPlusTreeNode leaf)
    {
        var mid = leaf.Keys.Count / 2;
        var newNode = new BPlusTreeNode(isLeaf: true);

        newNode.Keys.AddRange(leaf.Keys.GetRange(mid, leaf.Keys.Count - mid));
        newNode.Offsets.AddRange(leaf.Offsets.GetRange(mid, leaf.Offsets.Count - mid));

        leaf.Keys.RemoveRange(mid, leaf.Keys.Count - mid);
        leaf.Offsets.RemoveRange(mid, leaf.Offsets.Count - mid);

        newNode.Next = leaf.Next;
        leaf.Next = newNode;

        return (newNode, newNode.Keys[0]);
    }

    private (BPlusTreeNode Node, object Key) SplitInternal(BPlusTreeNode node)
    {
        var mid = node.Keys.Count / 2;
        var newNode = new BPlusTreeNode(isLeaf: false);

        var upKey = node.Keys[mid];

        newNode.Keys.AddRange(node.Keys.GetRange(mid + 1, node.Keys.Count - mid - 1));
        newNode.Children.AddRange(node.Children.GetRange(mid + 1, node.Children.Count - mid - 1));

        node.Keys.RemoveRange(mid, node.Keys.Count - mid);
        node.Children.RemoveRange(mid + 1, node.Children.Count - mid - 1);

        return (newNode, upKey);
    }

    private static int CompareKeys(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        if (a is IComparable ca && b is IComparable cb)
            return ca.CompareTo(cb);

        return (a.ToString() ?? string.Empty).CompareTo(b.ToString() ?? string.Empty);
    }
}
