"use client";
import { useEffect, useState } from "react";
import { CustomerSlaPresentation } from "./customer-sla-presentation";
import { CustomerSlaStatus, fetchCustomerSlaStatus } from "../lib/customer-sla-status";

export function CustomerSlaStatusPanel() {
  const [status, setStatus] = useState<CustomerSlaStatus | Readonly<{ state: "loading" }>>({ state: "loading" });
  useEffect(() => {
    let active = true;
    void fetchCustomerSlaStatus().then((next) => { if (active) setStatus(next); });
    return () => { active = false; };
  }, []);
  if (status.state === "loading") return <p role="status">Đang tải trạng thái SLA…</p>;
  if (status.state === "forbidden") return <p role="status">Bạn không có quyền xem trạng thái SLA.</p>;
  if (status.state === "unavailable") return <CustomerSlaPresentation data={null} />;
  return <CustomerSlaPresentation data={status.data} />;
}
