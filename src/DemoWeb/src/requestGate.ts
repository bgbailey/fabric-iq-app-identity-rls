interface RequestTicket {
  signal: AbortSignal;
  isCurrent: () => boolean;
}

export class RequestGate {
  private sequence = 0;
  private controller: AbortController | null = null;

  invalidate(): void {
    this.sequence += 1;
    this.controller?.abort();
    this.controller = null;
  }

  begin(): RequestTicket {
    this.invalidate();
    const sequence = this.sequence;
    const controller = new AbortController();
    this.controller = controller;
    return {
      signal: controller.signal,
      isCurrent: () => sequence === this.sequence && !controller.signal.aborted,
    };
  }
}
