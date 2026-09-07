/* ===========================================================================
   dbo.AnalystMemory — widen "kind" beyond devices and notes.

   The memory started as device aliases, because the warehouse knows hostnames
   and people know "Jason's phone". But the same mechanism is the right home for
   two other things the owner wants remembered:

     preference  how they want results presented. "Always order descending",
                 "show a chart when the data suits one", "give me 50 rows not
                 10". These go into the prompt as standing instructions, so they
                 do not have to be repeated every question.

     rename      a display substitution. Reverse DNS produces ALIEN01,
                 ALIEN01.bylotas.net and alien01.BYLOTAS.NET for one machine;
                 this collapses every variation to one label in the OUTPUT only.
                 It never touches the query or the stored data, so the numbers
                 are unchanged and the substitution is reversible.

   Idempotent: safe to run on a database that already has the two-kind version.
   ======================================================================== */

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.AnalystMemory', 'U') IS NULL
BEGIN
    RAISERROR('dbo.AnalystMemory does not exist. Run AnalystMemory.sql first.', 16, 1);
END
GO

-- The CHECK has to be replaced rather than altered.
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_AnalystMemory_kind'
             AND parent_object_id = OBJECT_ID('dbo.AnalystMemory'))
BEGIN
    ALTER TABLE dbo.AnalystMemory DROP CONSTRAINT CK_AnalystMemory_kind;
    PRINT 'dropped the old CK_AnalystMemory_kind';
END
GO

ALTER TABLE dbo.AnalystMemory WITH CHECK
    ADD CONSTRAINT CK_AnalystMemory_kind
        CHECK (kind IN ('device', 'note', 'preference', 'rename'));
PRINT 'kind now allows device, note, preference, rename';
GO

/* A rename needs something to match on, exactly as a device needs a target to
   filter on. A preference and a note do not: they are prose. */
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_AnalystMemory_device_target'
             AND parent_object_id = OBJECT_ID('dbo.AnalystMemory'))
BEGIN
    ALTER TABLE dbo.AnalystMemory DROP CONSTRAINT CK_AnalystMemory_device_target;
    PRINT 'dropped the old CK_AnalystMemory_device_target';
END
GO

ALTER TABLE dbo.AnalystMemory WITH CHECK
    ADD CONSTRAINT CK_AnalystMemory_target_required
        CHECK (kind NOT IN ('device', 'rename')
               OR (target IS NOT NULL AND LEN(target) > 0));
PRINT 'device and rename now both require a target';
GO

PRINT '--- current memory ---';
SELECT kind, entries = COUNT(*) FROM dbo.AnalystMemory GROUP BY kind ORDER BY kind;
GO
