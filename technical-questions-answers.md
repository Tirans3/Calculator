# Technical Questions & Answers

## SQL Questions

### 1) Group By with Multiple Fields (Group by մի քանի դաշտով)

```sql
-- Group by multiple columns
SELECT 
    CustomerID, 
    OrderDate, 
    COUNT(*) as OrderCount,
    SUM(TotalAmount) as TotalSales
FROM Orders
GROUP BY CustomerID, OrderDate
ORDER BY CustomerID, OrderDate;

-- Group by with expressions
SELECT 
    YEAR(OrderDate) as OrderYear,
    MONTH(OrderDate) as OrderMonth,
    ProductCategory,
    COUNT(*) as OrderCount,
    AVG(UnitPrice) as AveragePrice
FROM Orders o
JOIN Products p ON o.ProductID = p.ProductID
GROUP BY YEAR(OrderDate), MONTH(OrderDate), ProductCategory
HAVING COUNT(*) > 100;

-- Group by with CUBE and ROLLUP
SELECT 
    Region,
    ProductCategory,
    SUM(Sales) as TotalSales
FROM SalesData
GROUP BY CUBE(Region, ProductCategory);

-- Group by with GROUPING SETS
SELECT 
    Region,
    ProductCategory,
    SUM(Sales) as TotalSales
FROM SalesData
GROUP BY GROUPING SETS (
    (Region, ProductCategory),
    (Region),
    (ProductCategory),
    ()
);
```

### 2) Non-Clustered Index Storage in Memory (նո կլաստեռի պահելու ձևերը հիշողությունում)

```sql
-- Creating non-clustered indexes
CREATE NONCLUSTERED INDEX IX_Customer_Name 
ON Customers (LastName, FirstName)
INCLUDE (Email, Phone);

-- Index storage methods:
-- 1. Buffer Pool - most frequently used index pages
-- 2. Plan Cache - execution plans using indexes
-- 3. Index allocation map pages
-- 4. Index statistics in memory

-- Monitor index usage
SELECT 
    i.name as IndexName,
    s.user_seeks,
    s.user_scans,
    s.user_lookups,
    s.user_updates
FROM sys.dm_db_index_usage_stats s
JOIN sys.indexes i ON s.object_id = i.object_id AND s.index_id = i.index_id
WHERE s.database_id = DB_ID();

-- Memory usage by indexes
SELECT 
    i.name as IndexName,
    SUM(a.used_pages) * 8 as IndexSizeKB
FROM sys.indexes i
JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
JOIN sys.allocation_units a ON p.partition_id = a.container_id
GROUP BY i.name;
```

### 3) Uncommitted Data Storage Before Commit (uncommitted ուր է պահում մինչև commit անելը)

```sql
-- Transaction log stores uncommitted changes
BEGIN TRANSACTION;

UPDATE Products 
SET Price = Price * 1.1
WHERE CategoryID = 1;

-- Data is stored in:
-- 1. Transaction Log (log records)
-- 2. Buffer Pool (dirty pages)
-- 3. Lock manager (lock information)
-- 4. Version store (for snapshot isolation)

-- Check uncommitted transactions
SELECT 
    t.session_id,
    t.transaction_id,
    t.transaction_begin_time,
    t.transaction_state,
    t.transaction_type
FROM sys.dm_tran_active_transactions t
JOIN sys.dm_tran_session_transactions s ON t.transaction_id = s.transaction_id;

-- Check dirty pages in buffer pool
SELECT 
    database_id,
    page_id,
    page_type,
    is_modified
FROM sys.dm_os_buffer_descriptors
WHERE is_modified = 1;

COMMIT TRANSACTION;
```

### 4) Parallelism in DB Without Locking (parallelism in db without locking)

```sql
-- Optimistic Concurrency Control
-- 1. Timestamp/Version-based
ALTER TABLE Products ADD Version TIMESTAMP;

-- Update with version check
UPDATE Products 
SET Price = @NewPrice, 
    Version = Version + 1
WHERE ProductID = @ProductID 
  AND Version = @OriginalVersion;

-- 2. Snapshot Isolation
ALTER DATABASE MyDatabase 
SET ALLOW_SNAPSHOT_ISOLATION ON;

SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
BEGIN TRANSACTION;
-- Read consistent snapshot without blocking
SELECT * FROM Products;
COMMIT;

-- 3. Read Uncommitted (No shared locks)
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT * FROM Products; -- No blocking, but dirty reads possible

-- 4. MVCC (Multi-Version Concurrency Control)
-- SQL Server uses row versioning
ALTER DATABASE MyDatabase 
SET READ_COMMITTED_SNAPSHOT ON;

-- 5. Parallel execution hints
SELECT /*+ PARALLEL(4) */ 
    ProductID, 
    COUNT(*) 
FROM OrderDetails 
GROUP BY ProductID;

-- 6. Lock-free data structures in memory
-- Use atomic operations for counters
DECLARE @Counter BIGINT = 0;
-- Atomic increment without locks
SELECT @Counter = @Counter + 1;
```

## C# Questions

### 1) ASP.NET Framework Deadlock in Parallel (framework why will throw deadlock in parallel)

```csharp
// ASP.NET Framework deadlock scenario
public class FrameworkDeadlockExample
{
    // This causes deadlock in ASP.NET Framework
    public ActionResult DeadlockExample()
    {
        // SynchronizationContext captures ASP.NET context
        var result = SomeAsyncMethod().Result; // DEADLOCK!
        return View(result);
    }
    
    private async Task<string> SomeAsyncMethod()
    {
        await Task.Delay(1000); // Tries to resume on ASP.NET thread
        return "Data";
    }
    
    // Why deadlock happens:
    // 1. ASP.NET Framework uses AspNetSynchronizationContext
    // 2. Only one thread can execute per request context
    // 3. .Result blocks the thread
    // 4. Async method tries to resume on same blocked thread
    // 5. DEADLOCK!
}

// Solutions for Framework:
public class FrameworkSolutions
{
    // Solution 1: Use ConfigureAwait(false)
    public ActionResult Solution1()
    {
        var result = SomeAsyncMethodFixed().Result;
        return View(result);
    }
    
    private async Task<string> SomeAsyncMethodFixed()
    {
        await Task.Delay(1000).ConfigureAwait(false); // Don't capture context
        return "Data";
    }
    
    // Solution 2: Use async all the way
    public async Task<ActionResult> Solution2()
    {
        var result = await SomeAsyncMethod();
        return View(result);
    }
    
    // Solution 3: Use Task.Run
    public ActionResult Solution3()
    {
        var result = Task.Run(async () => await SomeAsyncMethod()).Result;
        return View(result);
    }
}
```

### 2) ASP.NET Core Will Not Deadlock (vs. core will not)

```csharp
// ASP.NET Core doesn't deadlock
public class CoreNoDeadlockExample : Controller
{
    // This works fine in ASP.NET Core
    public IActionResult NoDeadlockExample()
    {
        var result = SomeAsyncMethod().Result; // No deadlock in Core!
        return View(result);
    }
    
    private async Task<string> SomeAsyncMethod()
    {
        await Task.Delay(1000); // No special context to resume on
        return "Data";
    }
    
    // Why no deadlock in Core:
    // 1. No AspNetSynchronizationContext by default
    // 2. Uses TaskScheduler.Default
    // 3. Thread pool threads can handle continuation
    // 4. No single-threaded apartment model
}

// Comparison
public class FrameworkVsCore
{
    // Framework behavior
    public void FrameworkBehavior()
    {
        Console.WriteLine($"SynchronizationContext: {SynchronizationContext.Current?.GetType().Name}");
        // Output: AspNetSynchronizationContext
        
        Console.WriteLine($"Thread ID: {Thread.CurrentThread.ManagedThreadId}");
        // Always same thread for request
    }
    
    // Core behavior
    public void CoreBehavior()
    {
        Console.WriteLine($"SynchronizationContext: {SynchronizationContext.Current?.GetType().Name}");
        // Output: null
        
        Console.WriteLine($"Thread ID: {Thread.CurrentThread.ManagedThreadId}");
        // Can be different threads
    }
}
```

### 3) Process Context Storage (proc 1 core ուր է պահմ կիսատ թողած պռոցեսի հասցեն որից բդի շարունակե)

```csharp
// Process context is stored in Process Control Block (PCB)
public class ProcessContextExample
{
    // What gets stored during context switch:
    public struct ProcessContext
    {
        public ulong ProgramCounter;    // Next instruction address
        public ulong StackPointer;      // Stack position
        public ulong[] Registers;       // CPU register values
        public int ProcessId;           // Process ID
        public int ThreadId;            // Thread ID
        public ProcessState State;      // Running, Ready, Blocked
        public int Priority;            // Process priority
        public ulong[] FPURegisters;    // Floating point registers
    }
    
    // Where it's stored:
    // 1. Kernel space memory (PCB)
    // 2. Task State Segment (TSS) on x86
    // 3. Thread Information Block (TIB)
    // 4. Kernel stack
    
    // Example of context switch simulation
    public void ContextSwitchExample()
    {
        var process1 = new ProcessContext
        {
            ProgramCounter = 0x1000,
            StackPointer = 0x2000,
            ProcessId = 1234,
            State = ProcessState.Running
        };
        
        // Save current context
        SaveContext(process1);
        
        // Load next process context
        var process2 = LoadContext(5678);
        
        // Resume execution from saved address
        ResumeExecution(process2.ProgramCounter);
    }
    
    private void SaveContext(ProcessContext context)
    {
        // Save to PCB in kernel memory
        // Store registers, stack pointer, program counter
    }
    
    private ProcessContext LoadContext(int processId)
    {
        // Load from PCB
        return new ProcessContext();
    }
    
    private void ResumeExecution(ulong address)
    {
        // Jump to saved instruction address
    }
}

public enum ProcessState
{
    New,
    Ready,
    Running,
    Blocked,
    Terminated
}
```

### 4) Processor Caches L1, L2, L3 (պռոցի քեշերը L1, L2, L3)

```csharp
// Cache hierarchy demonstration
public class ProcessorCacheExample
{
    // Cache characteristics:
    // L1: 32KB, 1-4 cycles, per core
    // L2: 256KB-1MB, 10-20 cycles, per core
    // L3: 8-32MB, 40-75 cycles, shared
    
    public void CachePerformanceDemo()
    {
        const int arraySize = 64 * 1024 * 1024; // 64MB
        var array = new int[arraySize];
        
        // L1 cache friendly - sequential access
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < arraySize; i++)
        {
            array[i] = i;
        }
        sw.Stop();
        Console.WriteLine($"Sequential: {sw.ElapsedMilliseconds}ms");
        
        // Cache unfriendly - random access
        var random = new Random();
        sw.Restart();
        for (int i = 0; i < arraySize; i++)
        {
            int index = random.Next(arraySize);
            array[index] = i;
        }
        sw.Stop();
        Console.WriteLine($"Random: {sw.ElapsedMilliseconds}ms");
    }
    
    // Cache line optimization
    public struct CacheLineFriendly
    {
        public int Value1;
        public int Value2;
        public int Value3;
        public int Value4;
        // Total: 16 bytes, fits in cache line
    }
    
    public struct CacheLineUnfriendly
    {
        public int Value1;
        private readonly byte[] _padding1 = new byte[60];
        public int Value2;
        private readonly byte[] _padding2 = new byte[60];
        // Cache line thrashing
    }
    
    // False sharing example
    public class FalseSharingExample
    {
        // Bad: variables in same cache line
        private volatile int _counter1;
        private volatile int _counter2;
        
        // Good: pad to separate cache lines
        private volatile int _counter3;
        private readonly byte[] _padding = new byte[64];
        private volatile int _counter4;
        
        public void DemoFalseSharing()
        {
            Task.Run(() => {
                for (int i = 0; i < 100000000; i++)
                    _counter1++;
            });
            
            Task.Run(() => {
                for (int i = 0; i < 100000000; i++)
                    _counter2++;
            });
            // These will cause false sharing
        }
    }
}
```

## Algorithmic Tasks

### 1) Two Sum Problem - Unsorted Array O(n) (return true, եթե տրված չսորտավորված զանգվածի գոնե 2 թվերի գումարը հավասար է տրված թվին)

```csharp
public class TwoSumUnsorted
{
    // O(n) solution using HashSet
    public bool HasTwoSum(int[] nums, int target)
    {
        var seen = new HashSet<int>();
        
        foreach (int num in nums)
        {
            int complement = target - num;
            if (seen.Contains(complement))
            {
                return true;
            }
            seen.Add(num);
        }
        
        return false;
    }
    
    // Alternative: return indices
    public int[] TwoSumIndices(int[] nums, int target)
    {
        var map = new Dictionary<int, int>();
        
        for (int i = 0; i < nums.Length; i++)
        {
            int complement = target - nums[i];
            if (map.ContainsKey(complement))
            {
                return new int[] { map[complement], i };
            }
            map[nums[i]] = i;
        }
        
        return new int[0];
    }
    
    // Test cases
    public void TestTwoSum()
    {
        int[] nums1 = { 2, 7, 11, 15 };
        Console.WriteLine(HasTwoSum(nums1, 9));  // true (2 + 7)
        
        int[] nums2 = { 3, 2, 4 };
        Console.WriteLine(HasTwoSum(nums2, 6));  // true (2 + 4)
        
        int[] nums3 = { 3, 3 };
        Console.WriteLine(HasTwoSum(nums3, 6));  // true (3 + 3)
        
        int[] nums4 = { 1, 2, 3 };
        Console.WriteLine(HasTwoSum(nums4, 10)); // false
    }
}
```

### 2) Two Sum Problem - Sorted Array O(n) Without Extra Space (սորտավորածը առան հավելյալ տարածք օգտագործելու)

```csharp
public class TwoSumSorted
{
    // O(n) solution using two pointers, O(1) space
    public bool HasTwoSum(int[] sortedNums, int target)
    {
        int left = 0;
        int right = sortedNums.Length - 1;
        
        while (left < right)
        {
            int sum = sortedNums[left] + sortedNums[right];
            
            if (sum == target)
            {
                return true;
            }
            else if (sum < target)
            {
                left++;
            }
            else
            {
                right--;
            }
        }
        
        return false;
    }
    
    // Return indices (1-based as per LeetCode)
    public int[] TwoSumIndices(int[] sortedNums, int target)
    {
        int left = 0;
        int right = sortedNums.Length - 1;
        
        while (left < right)
        {
            int sum = sortedNums[left] + sortedNums[right];
            
            if (sum == target)
            {
                return new int[] { left + 1, right + 1 };
            }
            else if (sum < target)
            {
                left++;
            }
            else
            {
                right--;
            }
        }
        
        return new int[0];
    }
    
    // Test cases
    public void TestTwoSumSorted()
    {
        int[] nums1 = { 2, 7, 11, 15 };
        Console.WriteLine(HasTwoSum(nums1, 9));  // true (2 + 7)
        
        int[] nums2 = { 2, 3, 4 };
        Console.WriteLine(HasTwoSum(nums2, 6));  // true (2 + 4)
        
        int[] nums3 = { -1, 0 };
        Console.WriteLine(HasTwoSum(nums3, -1)); // true (-1 + 0)
        
        int[] nums4 = { 1, 2, 3, 4, 5 };
        Console.WriteLine(HasTwoSum(nums4, 10)); // false
    }
}
```

## Logic Problem

### Burning Thread Problem (մի թելը որը վառվում է միժամ վառելով ստանալ 15 րոպե)

```csharp
public class BurningThreadProblem
{
    /*
     * Problem: You have a thread that burns for exactly 1 hour.
     * You can light it from both ends. How do you measure 15 minutes?
     * 
     * Solution:
     * 1. Light the thread from both ends simultaneously
     * 2. It will burn completely in 30 minutes (since it burns twice as fast)
     * 3. At the same time, light another thread from one end
     * 4. When the first thread is completely burned (30 minutes), 
     *    the second thread has 30 minutes left
     * 5. Now light the second thread from the other end
     * 6. The second thread will burn the remaining 30 minutes in 15 minutes
     * 7. Total time: 30 + 15 = 45 minutes? No, this gives us 15 minutes measurement
     */
    
    public void SolutionExplanation()
    {
        Console.WriteLine("Solution to measure 15 minutes:");
        Console.WriteLine("1. Light Thread A from both ends");
        Console.WriteLine("2. Light Thread B from one end");
        Console.WriteLine("3. When Thread A burns out (30 min), Thread B has 30 min left");
        Console.WriteLine("4. Light Thread B from the other end");
        Console.WriteLine("5. Thread B burns remaining 30 min in 15 min");
        Console.WriteLine("6. Total time from step 3 to step 5: 15 minutes");
    }
    
    // Simulation of the solution
    public class ThreadSimulation
    {
        public class BurningThread
        {
            public int TotalLength { get; }
            public int BurnRateFromStart { get; set; }
            public int BurnRateFromEnd { get; set; }
            public int CurrentLength { get; set; }
            
            public BurningThread(int totalLength)
            {
                TotalLength = totalLength;
                CurrentLength = totalLength;
                BurnRateFromStart = 0;
                BurnRateFromEnd = 0;
            }
            
            public void LightFromStart()
            {
                BurnRateFromStart = 1;
            }
            
            public void LightFromEnd()
            {
                BurnRateFromEnd = 1;
            }
            
            public bool IsFullyBurned()
            {
                return CurrentLength <= 0;
            }
            
            public void BurnOneMinute()
            {
                CurrentLength -= (BurnRateFromStart + BurnRateFromEnd);
                if (CurrentLength < 0) CurrentLength = 0;
            }
        }
        
        public void RunSimulation()
        {
            // Each thread represents 60 units (60 minutes)
            var threadA = new BurningThread(60);
            var threadB = new BurningThread(60);
            
            int time = 0;
            
            // Step 1: Light Thread A from both ends, Thread B from one end
            threadA.LightFromStart();
            threadA.LightFromEnd();
            threadB.LightFromStart();
            
            Console.WriteLine($"Time {time}: Thread A lit from both ends, Thread B lit from one end");
            
            // Burn until Thread A is completely burned
            while (!threadA.IsFullyBurned())
            {
                time++;
                threadA.BurnOneMinute();
                threadB.BurnOneMinute();
                
                if (time % 10 == 0)
                {
                    Console.WriteLine($"Time {time}: Thread A length: {threadA.CurrentLength}, Thread B length: {threadB.CurrentLength}");
                }
            }
            
            Console.WriteLine($"Time {time}: Thread A completely burned, Thread B has {threadB.CurrentLength} minutes left");
            
            // Step 2: Light Thread B from the other end
            threadB.LightFromEnd();
            int measurementStart = time;
            
            Console.WriteLine($"Time {time}: Thread B now lit from both ends");
            
            // Burn until Thread B is completely burned
            while (!threadB.IsFullyBurned())
            {
                time++;
                threadB.BurnOneMinute();
            }
            
            int measurementTime = time - measurementStart;
            Console.WriteLine($"Time {time}: Thread B completely burned");
            Console.WriteLine($"Measurement time: {measurementTime} minutes");
            Console.WriteLine($"Success! We measured {measurementTime} minutes using 1-hour burning threads");
        }
    }
    
    // Run the simulation
    public void RunSolution()
    {
        var simulation = new ThreadSimulation();
        simulation.RunSimulation();
    }
}
```

## Summary

1. **SQL**: Covered GROUP BY, index storage, transaction handling, and lock-free parallelism
2. **C#**: Explained ASP.NET Framework vs Core deadlock differences and processor architecture
3. **Algorithms**: Provided O(n) solutions for Two Sum problem in both sorted and unsorted arrays
4. **Logic**: Solved the burning thread problem with detailed explanation and simulation

Each solution includes practical examples and explanations that you can use in real-world scenarios.