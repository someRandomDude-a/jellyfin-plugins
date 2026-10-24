"""Run in the disposable Python checker image; no production databases are used."""
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import unittest


class ReadOnlyDatabaseChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        self.database = self.root / 'test.db'
        self.code = pathlib.Path(__file__).with_name('check-fixture-databases.py').read_text().replace(
            "root = pathlib.Path('/fixture')", f'root = pathlib.Path({str(self.root)!r})')

    def seed(self, sql):
        with sqlite3.connect(self.database) as db:
            db.executescript(sql)

    def check(self):
        before = {p.name: p.read_bytes() for p in self.root.iterdir()}
        result = subprocess.run([sys.executable, '-c', self.code], capture_output=True, text=True)
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.root.iterdir()}, 'Checker changed original fixture files')
        return result

    def test_valid_database_is_not_changed(self):
        self.seed('CREATE TABLE Users(InternalId INTEGER, NormalizedUsername TEXT); INSERT INTO Users VALUES(1,"ALICE");')
        result = self.check()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('"integrity": "ok"', result.stdout)

    def test_duplicate_native_ids_are_rejected(self):
        self.seed('CREATE TABLE Users(InternalId INTEGER, NormalizedUsername TEXT); INSERT INTO Users VALUES(1,"ALICE"),(1,"BOB");')
        result = self.check()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Duplicate native internal user IDs', result.stderr)
        self.assertNotIn('ALICE', result.stdout + result.stderr)

    def test_dangling_foreign_key_is_rejected(self):
        self.seed('CREATE TABLE Parent(id INTEGER PRIMARY KEY); CREATE TABLE Child(parent INTEGER REFERENCES Parent(id)); INSERT INTO Child VALUES(99);')
        result = self.check()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Foreign-key violations', result.stderr)

    def test_empty_directory_is_not_a_pass(self):
        self.assertNotEqual(self.check().returncode, 0)


if __name__ == '__main__':
    unittest.main()
