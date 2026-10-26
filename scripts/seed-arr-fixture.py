"""Only run inside disposable, stopped suite-metadata-* containers' config volumes.
No media files, downloads, indexers or production databases are involved.
"""
import json
import sqlite3
import sys

source, profile = sys.argv[1], int(sys.argv[2])
assert source in ("sonarr", "radarr")
db = sqlite3.connect("/config/" + source + ".db")


def insert(table, values):
    columns = db.execute('PRAGMA table_info("' + table + '")').fetchall()
    assert columns, "Disposable schema unavailable"
    for _, name, kind, required, default, primary in columns:
        if name not in values and required and default is None and not primary:
            assert kind in ("INTEGER", "TEXT"), "Unexpected required fixture field"
            values[name] = 0 if kind == "INTEGER" else ""
    names = list(values)
    sql = 'INSERT INTO "' + table + '" (' + ",".join('"' + n + '"' for n in names) + ") VALUES(" + ",".join("?" for _ in names) + ")"
    return db.execute(sql, [values[n] for n in names]).lastrowid


common = dict(Title="Suite synthetic metadata", CleanTitle="suitesyntheticmetadata", SortTitle="suite synthetic metadata", Images="[]", Genres="[]", Ratings="{}", Year=2020, OriginalLanguage=1, Runtime=45, Status=1)
with db:
    if source == "sonarr":
        if not db.execute("SELECT Id FROM Series WHERE TvdbId=987654321").fetchone():
            series = insert("Series", dict(common, TvdbId=987654321, TitleSlug="suite-synthetic-metadata", Path="/test-media/Suite synthetic metadata", QualityProfileId=profile, Added="2020-01-01 00:00:00Z", Seasons=json.dumps([dict(seasonNumber=1, monitored=False)]), Tags="[]"))
            insert("Episodes", dict(SeriesId=series, SeasonNumber=1, EpisodeNumber=1, Title="Synthetic episode", EpisodeFileId=0, AirDateUtc="2020-01-01 00:00:00Z", AirDate="2020-01-01", Images="[]", Ratings="{}", Runtime=45, FinaleType="season"))
    else:
        existing = db.execute("SELECT Id FROM MovieMetadata WHERE TmdbId=987654321").fetchone()
        if not existing:
            metadata = insert("MovieMetadata", dict(common, TmdbId=987654321, Recommendations="[]", Keywords="[]"))
            insert("Movies", dict(MovieMetadataId=metadata, Path="/test-media/Suite synthetic metadata", QualityProfileId=profile, Added="2020-01-01 00:00:00Z", Tags="[]"))
print("Seeded isolated synthetic " + source + " records")
