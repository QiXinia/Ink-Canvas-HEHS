using InkCanvas.Core.Classroom;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>会话列表上限与显式清理：默认只列最近 20 条，删除必须由调用方显式触发。</summary>
public sealed class SessionRetentionTests
{
    [Fact]
    public void LoadAll_DefaultCap_ReturnsNewestTwentyInDescendingOrder()
    {
        string dir = TempDir();
        try
        {
            var created = SeedSessions(dir, 25);

            var loaded = ClassroomSessionStore.LoadAll(dir);

            Assert.Equal(ClassroomSessionStore.MaxSessionsLoaded, loaded.Count);
            Assert.Equal(20, loaded.Count);
            Assert.Equal(
                created.OrderByDescending(s => s.CreatedAt).Take(20).Select(s => s.Id),
                loaded.Select(s => s.Id));
            Assert.True(loaded[0].CreatedAt > loaded[^1].CreatedAt);
            // 列表截断不删除磁盘文件。
            Assert.Equal(25, CountSessionFiles(dir));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void LoadAll_WithCap_ReportsOmittedCountWithoutDeleting()
    {
        string dir = TempDir();
        try
        {
            SeedSessions(dir, 25);

            var loaded = ClassroomSessionStore.LoadAll(20, out int omitted, dir);

            Assert.Equal(20, loaded.Count);
            Assert.Equal(5, omitted);
            Assert.Equal(25, CountSessionFiles(dir));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void PruneOldSessions_ReturnsOldestIds_ButKeepsFiles()
    {
        string dir = TempDir();
        try
        {
            var created = SeedSessions(dir, 25);

            var pruned = ClassroomSessionStore.PruneOldSessions(20, dir);

            Assert.Equal(5, pruned.Count);
            Assert.Equal(
                created.OrderByDescending(s => s.CreatedAt).Skip(20).Select(s => s.Id),
                pruned);
            // 只返回候选，不自动删除。
            Assert.All(created, s => Assert.True(File.Exists(ClassroomSessionStore.PathFor(s.Id, dir))));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void DeleteOldSessions_RemovesOnlyTheOldestFive_WithJournalAndSnapshot()
    {
        string dir = TempDir();
        try
        {
            var created = SeedSessions(dir, 25);
            var ordered = created.OrderBy(s => s.CreatedAt).ToList();
            var oldest = ordered[0];
            var newest = ordered[^1];

            ClassroomSessionStore.SaveSnapshot(oldest, dir);
            ClassroomSessionStore.SaveSnapshot(newest, dir);
            using (var journal = SessionJournal.Open(oldest.Id, dir, oldest.Name, oldest.CreatedAt))
                journal.Append(new TimelineEvent { TimeMs = 1, PageIndex = 0, Kind = TimelineEventKind.PageChanged });
            using (var journal = SessionJournal.Open(newest.Id, dir, newest.Name, newest.CreatedAt))
                journal.Append(new TimelineEvent { TimeMs = 1, PageIndex = 0, Kind = TimelineEventKind.PageChanged });

            int removed = ClassroomSessionStore.DeleteOldSessions(20, dir);

            Assert.Equal(5, removed);
            foreach (var old in ordered.Take(5))
            {
                Assert.False(File.Exists(ClassroomSessionStore.PathFor(old.Id, dir)));
                Assert.False(File.Exists(ClassroomSessionStore.PathForSnapshot(old.Id, dir)));
                Assert.False(File.Exists(SessionJournal.PathFor(old.Id, dir)));
            }
            foreach (var kept in ordered.Skip(5))
                Assert.True(File.Exists(ClassroomSessionStore.PathFor(kept.Id, dir)));

            Assert.True(File.Exists(ClassroomSessionStore.PathForSnapshot(newest.Id, dir)));
            Assert.True(File.Exists(SessionJournal.PathFor(newest.Id, dir)));
            Assert.Equal(20, CountSessionFiles(dir));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void RetentionBoundaries_DoNotThrow()
    {
        string dir = TempDir();
        try
        {
            SeedSessions(dir, 25);

            Assert.Empty(ClassroomSessionStore.LoadAll(0, dir));
            Assert.Empty(ClassroomSessionStore.LoadAll(-5, dir));
            Assert.Equal(25, ClassroomSessionStore.LoadAll(int.MaxValue, dir).Count);

            Assert.Equal(25, ClassroomSessionStore.PruneOldSessions(0, dir).Count);
            Assert.Empty(ClassroomSessionStore.PruneOldSessions(int.MaxValue, dir));
            Assert.Equal(0, ClassroomSessionStore.DeleteOldSessions(int.MaxValue, dir));
            Assert.Equal(25, CountSessionFiles(dir));

            // 空目录同样不抛。
            string empty = TempDir();
            try
            {
                Assert.Empty(ClassroomSessionStore.LoadAll(20, empty));
                Assert.Empty(ClassroomSessionStore.PruneOldSessions(20, empty));
                Assert.Equal(0, ClassroomSessionStore.DeleteOldSessions(20, empty));
            }
            finally { DeleteTemp(empty); }
        }
        finally { DeleteTemp(dir); }
    }

    private static List<ClassroomSession> SeedSessions(string dir, int count)
    {
        var sessions = new List<ClassroomSession>();
        var start = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Local);
        for (int i = 0; i < count; i++)
        {
            var session = new ClassroomSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"session-{i:00}",
                CreatedAt = start.AddMinutes(i),
                Events = new List<TimelineEvent>
                {
                    new() { TimeMs = i, PageIndex = 0, Kind = TimelineEventKind.PageChanged },
                },
                Checkpoints = new List<SessionCheckpoint>(),
                Bookmarks = new List<SessionBookmark>(),
            };
            ClassroomSessionStore.Save(session, dir);
            sessions.Add(session);
        }
        return sessions;
    }

    private static int CountSessionFiles(string dir)
        => Directory.EnumerateFiles(dir, "*.json")
            .Count(path => !path.EndsWith(".snapshot.json", StringComparison.OrdinalIgnoreCase));

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTemp(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}