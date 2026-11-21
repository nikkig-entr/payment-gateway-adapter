-- Payment Gateway Adapter - Database Initialization
-- Idempotency store and reconciliation tables

-- Create extension for UUID generation
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Idempotency keys table (for payment deduplication)
CREATE TABLE IF NOT EXISTS idempotency_keys (
    id BIGSERIAL PRIMARY KEY,
    key_hash VARCHAR(64) NOT NULL UNIQUE,  -- SHA256 of idempotency key
    idempotency_key VARCHAR(128) NOT NULL,
    provider_name VARCHAR(50) NOT NULL,
    request_payload JSONB NOT NULL,
    response_payload JSONB,
    status VARCHAR(20) NOT NULL DEFAULT 'pending', -- pending, completed, failed
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_idempotency_keys_hash ON idempotency_keys(key_hash);
CREATE INDEX IF NOT EXISTS ix_idempotency_keys_expires ON idempotency_keys(expires_at);
CREATE INDEX IF NOT EXISTS ix_idempotency_keys_provider ON idempotency_keys(provider_name);

-- Reconciliation queue (for matching gateway transactions with internal orders)
CREATE TABLE IF NOT EXISTS reconciliation_queue (
    id BIGSERIAL PRIMARY KEY,
    idempotency_key VARCHAR(128) NOT NULL,
    provider_name VARCHAR(50) NOT NULL,
    provider_transaction_id VARCHAR(128) NOT NULL,
    order_id VARCHAR(128) NOT NULL,
    amount_minor_units BIGINT NOT NULL,
    currency VARCHAR(3) NOT NULL DEFAULT 'UAH',
    status VARCHAR(20) NOT NULL DEFAULT 'pending', -- pending, matched, mismatched, resolved
    gateway_data JSONB,
    internal_data JSONB,
    discrepancy_details TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    matched_at TIMESTAMPTZ,
    resolved_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_reconciliation_queue_provider_txn ON reconciliation_queue(provider_transaction_id);
CREATE INDEX IF NOT EXISTS ix_reconciliation_queue_order ON reconciliation_queue(order_id);
CREATE INDEX IF NOT EXISTS ix_reconciliation_queue_status ON reconciliation_queue(status);
CREATE INDEX IF NOT EXISTS ix_reconciliation_queue_created ON reconciliation_queue(created_at);

-- Webhook events log (for audit and debugging)
CREATE TABLE IF NOT EXISTS webhook_events (
    id BIGSERIAL PRIMARY KEY,
    provider_name VARCHAR(50) NOT NULL,
    event_type VARCHAR(50) NOT NULL,
    provider_transaction_id VARCHAR(128),
    order_id VARCHAR(128),
    payload JSONB NOT NULL,
    headers JSONB,
    signature_verified BOOLEAN NOT NULL DEFAULT false,
    processed BOOLEAN NOT NULL DEFAULT false,
    processing_error TEXT,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_webhook_events_provider_txn ON webhook_events(provider_transaction_id);
CREATE INDEX IF NOT EXISTS ix_webhook_events_order ON webhook_events(order_id);
CREATE INDEX IF NOT EXISTS ix_webhook_events_received ON webhook_events(received_at);
CREATE INDEX IF NOT EXISTS ix_webhook_events_processed ON webhook_events(processed);

-- Gateway statements (for daily reconciliation)
CREATE TABLE IF NOT EXISTS gateway_statements (
    id BIGSERIAL PRIMARY KEY,
    provider_name VARCHAR(50) NOT NULL,
    statement_date DATE NOT NULL,
    transaction_id VARCHAR(128) NOT NULL,
    order_id VARCHAR(128),
    amount_minor_units BIGINT NOT NULL,
    currency VARCHAR(3) NOT NULL DEFAULT 'UAH',
    transaction_type VARCHAR(20) NOT NULL, -- payment, refund, reversal, fee, payout
    status VARCHAR(20) NOT NULL, -- completed, pending, failed, cancelled
    fee_minor_units BIGINT,
    description TEXT,
    card_mask VARCHAR(20),
    raw_data JSONB,
    created_at TIMESTAMPTZ NOT NULL,
    settled_at TIMESTAMPTZ,
    imported_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    matched BOOLEAN NOT NULL DEFAULT false,
    matched_order_id VARCHAR(128)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_gateway_statements_provider_txn_date
    ON gateway_statements(provider_name, transaction_id, statement_date);
CREATE INDEX IF NOT EXISTS ix_gateway_statements_date ON gateway_statements(statement_date);
CREATE INDEX IF NOT EXISTS ix_gateway_statements_order ON gateway_statements(order_id);
CREATE INDEX IF NOT EXISTS ix_gateway_statements_matched ON gateway_statements(matched);

-- Function to clean expired idempotency keys
CREATE OR REPLACE FUNCTION clean_expired_idempotency_keys()
RETURNS INTEGER AS $$
DECLARE
    deleted_count INTEGER;
BEGIN
    DELETE FROM idempotency_keys
    WHERE expires_at < NOW()
    RETURNING 1 INTO deleted_count;

    GET DIAGNOSTICS deleted_count = ROW_COUNT;
    RETURN deleted_count;
END;
$$ LANGUAGE plpgsql;

-- Function to clean old webhook events (keep 90 days)
CREATE OR REPLACE FUNCTION clean_old_webhook_events()
RETURNS INTEGER AS $$
DECLARE
    deleted_count INTEGER;
BEGIN
    DELETE FROM webhook_events
    WHERE received_at < NOW() - INTERVAL '90 days'
    AND processed = true
    RETURNING 1 INTO deleted_count;

    GET DIAGNOSTICS deleted_count = ROW_COUNT;
    RETURN deleted_count;
END;
$$ LANGUAGE plpgsql;

-- Grant permissions
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO postgres;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO postgres;
GRANT EXECUTE ON FUNCTION clean_expired_idempotency_keys() TO postgres;
GRANT EXECUTE ON FUNCTION clean_old_webhook_events() TO postgres;

-- Insert sample data for testing
INSERT INTO idempotency_keys (key_hash, idempotency_key, provider_name, request_payload, response_payload, status, expires_at, completed_at)
VALUES
(sha256('ORDER-001:1'::bytea), 'ORDER-001:1', 'monobank',
 '{"orderId":"ORDER-001","amountMinorUnits":10000,"currency":"UAH","description":"Test payment"}',
 '{"success":true,"transactionId":"inv_abc123","paymentUrl":"https://pay.monobank.ua/inv_abc123"}',
 'completed', NOW() + INTERVAL '30 days', NOW())
ON CONFLICT (key_hash) DO NOTHING;

ANALYZE;