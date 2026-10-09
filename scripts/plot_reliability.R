#!/usr/bin/env Rscript
# Base R plots of observed samples only. Does not generate or modify CSV rows.
args <- commandArgs(trailingOnly = TRUE)
csv_path <- if (length(args) >= 1) args[1] else "docs/reliability_samples.csv"
out_dir <- if (length(args) >= 2) args[2] else "docs/graphs"
d <- read.csv(csv_path, stringsAsFactors = FALSE)
stopifnot(nrow(d) >= 300, all(is.finite(d$attempts)), all(is.finite(d$final_rto_ms)))
dir.create(out_dir, recursive = TRUE, showWarnings = FALSE)
series <- c("baseline", "loss_5", "loss_10", "loss_20")
x <- c(0, 5, 10, 20)
y <- vapply(series, function(s) mean(d$attempts[d$series == s]), numeric(1))
png(file.path(out_dir, "avg_attempts_vs_loss.png"), width = 1500, height = 900, res = 150, type = if (Sys.info()[["sysname"]] == "Darwin") "quartz" else "cairo")
par(mar = c(5.2, 5.3, 4.5, 2), family = "sans", las = 1)
plot(x, y, type = "n", xlim = c(-1, 21), ylim = c(0.96, max(y) + 0.12), xaxt = "n",
     xlab = "Packet loss per direction, %", ylab = "Average attempts per command",
     main = "Reliable SHOOT: attempts vs packet loss", cex.main = 1.2)
axis(1, at = x)
grid(col = "#dce3eb")
lines(x, y, type = "b", pch = 19, lwd = 3, col = "#2463aa")
text(x, y + 0.04, sprintf("%.2f", y), cex = 1.1, col = "#17395e")
mtext("50 commands per series; retransmissions keep the original sequence; maxAttempts = 5", side = 3, line = 0.4, cex = 0.8)
dev.off()

j <- d[d$series == "jitter_loss_10", ]
stopifnot(nrow(j) >= 50)
png(file.path(out_dir, "rto_jitter_loss_10.png"), width = 1500, height = 900, res = 150, type = if (Sys.info()[["sysname"]] == "Darwin") "quartz" else "cairo")
par(mar = c(5.2, 5.3, 4.5, 2), family = "sans", las = 1)
plot(j$command_index, j$final_rto_ms, type = "n", ylim = range(c(j$final_rto_ms, j$rto_before_send_ms)) + c(-20, 35),
     xlab = "Command index", ylab = "RTO, ms", main = "Adaptive RTO: jitter + 10% loss", cex.main = 1.2)
grid(col = "#dce3eb")
lines(j$command_index, j$rto_before_send_ms, col = "#8696a5", lwd = 1.5, lty = 2)
lines(j$command_index, j$final_rto_ms, col = "#2463aa", lwd = 2.5)
points(j$command_index[j$attempts > 1], j$final_rto_ms[j$attempts > 1], col = "#b14b32", pch = 17, cex = 1.1)
legend("topright", c("After command", "Before SHOOT (after probe)", "Retransmitted: ACK sample excluded"),
       col = c("#2463aa", "#8696a5", "#b14b32"), lty = c(1, 2, NA), pch = c(NA, NA, 17), lwd = c(2.5, 1.5, NA), bg = "white", cex = 0.85)
mtext("20-150 ms jitter in each direction; Karn filters retransmitted ACK samples", side = 3, line = 0.4, cex = 0.8)
dev.off()
