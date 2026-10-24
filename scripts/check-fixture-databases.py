"""Read-only checks for disposable integration fixtures; never repairs a database."""
import json
import pathlib
import sqlite3
import shutil
import tempfile

root = pathlib.Path('/fixture')
results = []
for path in sorted(root.rglob('*')):
    if not path.is_file() or path.suffix.lower() not in ('.db', '.sqlite', '.sqlite3'):
        continue
    with path.open('rb') as stream:
        if stream.read(16) != b'SQLite format 3\x00':
            continue
    # WAL readers may need a writable shared-memory sidecar. Copy the complete stopped
    # database family to ephemeral storage; the mounted source remains strictly read-only.
    with tempfile.TemporaryDirectory() as temporary:
        copy = pathlib.Path(temporary) / path.name
        shutil.copyfile(path, copy)
        for suffix in ('-wal', '-shm', '-journal'):
            sidecar = pathlib.Path(str(path) + suffix)
            if sidecar.exists():
                shutil.copyfile(sidecar, pathlib.Path(str(copy) + suffix))
        with sqlite3.connect(copy.as_uri() + '?mode=ro', uri=True) as db:
            db.execute('PRAGMA query_only=ON')
            assert db.execute('PRAGMA integrity_check').fetchall() == [('ok',)], 'SQLite integrity check failed (details withheld)'
            assert not db.execute('PRAGMA foreign_key_check').fetchall(), 'Foreign-key violations (details withheld)'
            tables = {row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            if 'Users' in tables:
                assert not db.execute('SELECT InternalId FROM Users GROUP BY InternalId HAVING COUNT(*) > 1').fetchall(), 'Duplicate native internal user IDs'
                assert not db.execute('SELECT NormalizedUsername FROM Users GROUP BY NormalizedUsername HAVING COUNT(*) > 1').fetchall(), 'Duplicate normalized native usernames'
            results.append({'database': str(path.relative_to(root)), 'integrity': 'ok', 'foreignKeys': 'ok'})
assert results, 'No fixture SQLite databases found'
print(json.dumps(results))
