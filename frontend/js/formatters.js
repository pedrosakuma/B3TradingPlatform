const LOCALE = "pt-BR";
const UTC = "UTC";

const QUANTITY_FORMATTER = new Intl.NumberFormat(LOCALE, {
  maximumFractionDigits: 0,
});
const PRICE_FORMATTER = new Intl.NumberFormat(LOCALE, {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const DECIMAL_FORMATTER = new Intl.NumberFormat(LOCALE, {
  maximumFractionDigits: 4,
});
const CURRENCY_FORMATTER = new Intl.NumberFormat(LOCALE, {
  style: "currency",
  currency: "BRL",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const SIGNED_CURRENCY_FORMATTER = new Intl.NumberFormat(LOCALE, {
  style: "currency",
  currency: "BRL",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
  signDisplay: "exceptZero",
});
const PERCENT_FORMATTER = new Intl.NumberFormat(LOCALE, {
  style: "percent",
  minimumFractionDigits: 1,
  maximumFractionDigits: 1,
});
const UTC_TIME_FORMATTERS = new Map();
const UTC_DATE_TIME_FORMATTERS = new Map();
const UTC_DATE_FORMATTER = new Intl.DateTimeFormat(LOCALE, {
  timeZone: UTC,
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
});
const DAY_MONTH_FORMATTER = new Intl.DateTimeFormat(LOCALE, {
  timeZone: UTC,
  day: "2-digit",
  month: "short",
});

function numericValue(value) {
  if (value == null || value === "") return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function dateValue(value) {
  if (value == null || value === "") return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

export function formatQuantity(value, fallback = "—") {
  const number = numericValue(value);
  return number == null ? fallback : QUANTITY_FORMATTER.format(number);
}

export function formatPrice(value, fallback = "—") {
  const number = numericValue(value);
  return number == null ? fallback : PRICE_FORMATTER.format(number);
}

export function formatDecimal(value, fallback = "—") {
  const number = numericValue(value);
  return number == null ? fallback : DECIMAL_FORMATTER.format(number);
}

export function formatCurrency(value, fallback = "R$ —") {
  const number = numericValue(value);
  return number == null ? fallback : CURRENCY_FORMATTER.format(number);
}

export function formatSignedCurrency(value, fallback = "—") {
  const number = numericValue(value);
  return number == null ? fallback : SIGNED_CURRENCY_FORMATTER.format(number);
}

export function formatPercent(value, fallback = "—") {
  const number = numericValue(value);
  return number == null ? fallback : PERCENT_FORMATTER.format(number);
}

export function formatUtcTime(value, {
  seconds = true,
  fractionalSecondDigits,
  fallback = "—",
} = {}) {
  const date = dateValue(value);
  if (!date) return fallback;
  const key = `${seconds}:${fractionalSecondDigits ?? 0}`;
  let formatter = UTC_TIME_FORMATTERS.get(key);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat(LOCALE, {
      timeZone: UTC,
      hour: "2-digit",
      minute: "2-digit",
      second: seconds ? "2-digit" : undefined,
      fractionalSecondDigits,
      hourCycle: "h23",
    });
    UTC_TIME_FORMATTERS.set(key, formatter);
  }
  return formatter.format(date);
}

export function formatUtcDateTime(value, {
  seconds = true,
  fallback = "—",
} = {}) {
  const date = dateValue(value);
  if (!date) return fallback;
  const key = String(seconds);
  let formatter = UTC_DATE_TIME_FORMATTERS.get(key);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat(LOCALE, {
      timeZone: UTC,
      day: "2-digit",
      month: "2-digit",
      year: "numeric",
      hour: "2-digit",
      minute: "2-digit",
      second: seconds ? "2-digit" : undefined,
      hourCycle: "h23",
    });
    UTC_DATE_TIME_FORMATTERS.set(key, formatter);
  }
  return `${formatter.format(date)} UTC`;
}

export function formatUtcDate(value, fallback = "—") {
  const date = dateValue(value);
  return date ? UTC_DATE_FORMATTER.format(date) : fallback;
}

export function formatDayMonth(value, fallback = "—") {
  const date = dateValue(value);
  return date ? DAY_MONTH_FORMATTER.format(date).replace(".", "") : fallback;
}
