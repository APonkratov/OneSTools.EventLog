-- Contract snapshot from upstream c95a2bf (not generated from ClickHouseStorage).
CREATE TABLE {database}.EventLogItems
(
    FileName LowCardinality(String),
    EndPosition Int64 CODEC(DoubleDelta, LZ4),
    LgfEndPosition Int64 CODEC(DoubleDelta, LZ4),
    Id Int64 CODEC(DoubleDelta, LZ4),
    DateTime DateTime('UTC') CODEC(Delta, LZ4),
    TransactionStatus LowCardinality(String),
    TransactionDate DateTime('UTC') CODEC(Delta, LZ4),
    TransactionNumber Int64 CODEC(DoubleDelta, LZ4),
    UserUuid LowCardinality(String),
    User LowCardinality(String),
    Computer LowCardinality(String),
    Application LowCardinality(String),
    Connection Int64 CODEC(DoubleDelta, LZ4),
    Event LowCardinality(String),
    Severity LowCardinality(String),
    Comment String CODEC(ZSTD),
    MetadataUuid String CODEC(ZSTD),
    Metadata LowCardinality(String),
    Data String CODEC(ZSTD),
    DataPresentation String CODEC(ZSTD),
    Server LowCardinality(String),
    MainPort Int32 CODEC(DoubleDelta, LZ4),
    AddPort Int32 CODEC(DoubleDelta, LZ4),
    Session Int64 CODEC(DoubleDelta, LZ4)
)
ENGINE = MergeTree()
PARTITION BY (toYYYYMM(DateTime))
ORDER BY (DateTime, EndPosition)
SETTINGS index_granularity = 8192
