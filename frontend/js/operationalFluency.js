const AUCTION_PHASES = new Set(["OpeningCall", "FinalClosingCall"]);

function titleCase(value) {
  return String(value ?? "")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/_/g, " ")
    .replace(/^./, (char) => char.toUpperCase());
}

function websocketSignal(status) {
  if (status === "connected") {
    return {
      key: "orders",
      label: "Order updates",
      value: "Live",
      tone: "ok",
      detail: "Working Orders and Executions are receiving live updates.",
    };
  }
  if (status === "connecting") {
    return {
      key: "orders",
      label: "Order updates",
      value: "Connecting",
      tone: "warning",
      detail: "Live order updates may be delayed while the WebSocket connects.",
    };
  }
  return {
    key: "orders",
    label: "Order updates",
    value: "Offline",
    tone: "warning",
    detail: "Live order updates are unavailable; submitted orders may appear later.",
  };
}

function gatewaySignal(gatewayHealth) {
  if (gatewayHealth?.error) {
    return {
      key: "gateway",
      label: "Exchange gateway",
      value: "Unreachable",
      tone: "danger",
      detail: "The gateway health check failed; the platform may reject new orders.",
    };
  }
  if (!gatewayHealth || !Array.isArray(gatewayHealth.firms)) {
    return {
      key: "gateway",
      label: "Exchange gateway",
      value: "Unknown",
      tone: "warning",
      detail: "Gateway readiness is not exposed by this host; the server still validates every submit.",
    };
  }
  if (gatewayHealth.firms.length === 0) {
    return {
      key: "gateway",
      label: "Exchange gateway",
      value: "No firms",
      tone: "danger",
      detail: "No exchange firm session is configured for order routing.",
    };
  }

  const allEstablished = gatewayHealth.firms.every((firm) => firm.state === "established");
  if (allEstablished && gatewayHealth.readyForOrders) {
    return {
      key: "gateway",
      label: "Exchange gateway",
      value: "Established",
      tone: "ok",
      detail: "The exchange firm session reports ready for orders.",
    };
  }
  if (gatewayHealth.firms.some((firm) => firm.reconnecting)) {
    return {
      key: "gateway",
      label: "Exchange gateway",
      value: "Reconnecting",
      tone: "warning",
      detail: "The exchange firm session is reconnecting; wait for it to establish.",
    };
  }

  const unavailable = gatewayHealth.firms.find((firm) => firm.state !== "established")
    ?? gatewayHealth.firms[0];
  return {
    key: "gateway",
    label: "Exchange gateway",
    value: titleCase(unavailable.state || "Unavailable"),
    tone: "danger",
    detail: `The exchange firm session is ${unavailable.state || "unavailable"}; the platform may reject new orders.`,
  };
}

function marketDataSignal(status) {
  if (status === "connected") {
    return {
      key: "marketData",
      label: "Market data",
      value: "Live",
      tone: "ok",
      detail: "Prices, depth, chart, and tape are receiving live market data.",
    };
  }
  if (status === "connecting") {
    return {
      key: "marketData",
      label: "Market data",
      value: "Connecting",
      tone: "warning",
      detail: "Market context is still connecting; review price and quantity before submitting.",
    };
  }
  if (status === "not_ready") {
    return {
      key: "marketData",
      label: "Market data",
      value: "Unavailable",
      tone: "warning",
      detail: "The feed reported unavailable; review price and quantity without live market context.",
    };
  }
  return {
    key: "marketData",
    label: "Market data",
    value: "Offline",
    tone: "warning",
    detail: "Live market context is offline; review price and quantity before submitting.",
  };
}

function phaseSignal(symbol, phase) {
  if (!symbol) {
    return {
      key: "phase",
      label: "Session phase",
      value: "Select symbol",
      tone: "neutral",
      detail: "Enter a symbol to check its current trading phase.",
    };
  }
  if (phase === "Reserved") {
    return {
      key: "phase",
      label: "Session phase",
      value: "Halted",
      tone: "danger",
      detail: `${symbol} is Reserved; the existing phase rule disables Submit.`,
    };
  }
  if (!phase || phase === "Unknown") {
    return {
      key: "phase",
      label: "Session phase",
      value: "Unknown",
      tone: "warning",
      detail: `${symbol}'s phase is unavailable; venue rules still apply.`,
    };
  }
  if (AUCTION_PHASES.has(phase)) {
    return {
      key: "phase",
      label: "Session phase",
      value: titleCase(phase),
      tone: "warning",
      detail: `${symbol} is in ${titleCase(phase)}; GoodForAuction is recommended and Day remains pending until the cross.`,
    };
  }
  if (phase === "Open") {
    return {
      key: "phase",
      label: "Session phase",
      value: "Open",
      tone: "ok",
      detail: `${symbol} is in the continuous trading session.`,
    };
  }
  return {
    key: "phase",
    label: "Session phase",
    value: titleCase(phase),
    tone: "neutral",
    detail: `${symbol} is in ${titleCase(phase)}; review the applicable time-in-force rules.`,
  };
}

export function deriveTradingReadiness({
  status,
  gatewayHealth,
  marketDataStatus,
  symbol,
  phase,
} = {}) {
  const signals = [
    websocketSignal(status),
    gatewaySignal(gatewayHealth),
    marketDataSignal(marketDataStatus),
    phaseSignal(symbol, phase),
  ];
  const halted = symbol && phase === "Reserved";
  const gatewayUnavailable = signals.find((signal) =>
    signal.key === "gateway" && signal.tone === "danger");
  const reviewSignal = signals.find((signal) => signal.tone === "warning");

  if (halted) {
    return {
      tone: "danger",
      title: "Submit blocked",
      message: `${symbol} is Reserved. Submit remains disabled by the existing phase rule.`,
      signals,
    };
  }
  if (gatewayUnavailable) {
    return {
      tone: "danger",
      title: "Venue unavailable",
      message: `${gatewayUnavailable.detail} Submit availability is unchanged; the server remains authoritative.`,
      signals,
    };
  }
  if (reviewSignal || !symbol) {
    return {
      tone: "warning",
      title: "Review before submitting",
      message: (reviewSignal ?? signals.at(-1)).detail,
      signals,
    };
  }
  return {
    tone: "ok",
    title: "Ready for live trading",
    message: "Connections are healthy and the selected symbol is open.",
    signals,
  };
}

export function deriveTraderEmptyState(surface, context = {}) {
  switch (surface) {
    case "chart":
      if (!context.symbol) {
        return {
          title: "Select a symbol",
          detail: "Candles for the active symbol and resolution appear here.",
        };
      }
      if (context.timedOut) {
        return {
          title: "No candle data received",
          detail: "Check Market data settings or choose another subscribed symbol.",
        };
      }
      if (context.snapshotReady) {
        return {
          title: "No candles yet",
          detail: "The first closed interval appears after trades arrive.",
        };
      }
      return {
        title: "Waiting for candle history",
        detail: `The ${context.symbol} snapshot will appear when the feed responds.`,
      };

    case "book":
      if (context.side) {
        return {
          title: `No ${context.side} levels`,
          detail: "The current book snapshot has no levels on this side.",
        };
      }
      if (!context.symbol) {
        return {
          title: "Select a symbol",
          detail: "Live bid and ask levels for the active symbol appear here.",
        };
      }
      if (context.timedOut) {
        return {
          title: "No book data received",
          detail: "Check Market data settings and confirm MBP is enabled for this symbol.",
        };
      }
      return {
        title: "Waiting for the book",
        detail: `The ${context.symbol} bid and ask snapshot will appear when the feed responds.`,
      };

    case "tape":
      if (!context.showAll && !context.symbol) {
        return {
          title: "Select a symbol",
          detail: "Recent trades for the active symbol appear here.",
        };
      }
      if (context.showAll) {
        return {
          title: "No trades received yet",
          detail: "Prints from subscribed symbols appear here as the feed publishes them.",
        };
      }
      return {
        title: `No trades for ${context.symbol}`,
        detail: "Turn on All symbols to monitor prints across the watchlist.",
      };

    case "orders":
      if (context.filtered) {
        return {
          title: "No orders match this view",
          detail: "Adjust the symbol or status filters, or turn off Working only.",
        };
      }
      return {
        title: "No working orders yet",
        detail: "Submit an order with the ticket above to track its live status here.",
      };

    case "executions":
      if (context.filtered) {
        return {
          title: "No executions match this filter",
          detail: "Clear or change the symbol filter to restore the live log.",
        };
      }
      return {
        title: "No executions yet",
        detail: "Acknowledgements, fills, cancels, and rejects appear here after order activity.",
      };

    default:
      throw new Error(`Unknown trader empty-state surface: ${surface}`);
  }
}

export function deriveOrderSubmitFeedback({ clOrdId, status, live = false } = {}) {
  const id = String(clOrdId ?? "").trim();
  if (!id) throw new Error("Order feedback requires a ClOrdID");
  if (!live) {
    return {
      tone: "info",
      message: `Platform accepted order ${id}. Waiting for its live order update in Working Orders.`,
    };
  }
  const normalizedStatus = String(status || "updated");
  return {
    tone: normalizedStatus === "Rejected" ? "error" : "ok",
    message: `Live order update received: order ${id} is ${normalizedStatus}.`,
  };
}
