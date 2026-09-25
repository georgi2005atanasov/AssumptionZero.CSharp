CREATE TABLE recorded_messages (
    id               BIGSERIAL PRIMARY KEY,
    recording_id     UUID NOT NULL,
    sequence_number  BIGINT NOT NULL,
    elapsed_ns       BIGINT NOT NULL,
    message_type     SMALLINT NOT NULL, -- binary or text
    payload          BYTEA NOT NULL,
    UNIQUE (recording_id, sequence_number)
);