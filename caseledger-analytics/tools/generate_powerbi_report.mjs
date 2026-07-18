import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(scriptDir, "..");
const reportRoot = path.join(
  projectRoot,
  "powerbi",
  "CaseLedgerAnalytics.Report"
);
const pagesRoot = path.join(reportRoot, "definition", "pages");
const visualSchema =
  "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/visualContainer/2.9.0/schema.json";

const colors = {
  navy: "#0B1F33",
  blue: "#2F6BFF",
  teal: "#007C83",
  amber: "#D97706",
  red: "#C73E1D",
  green: "#138A5B",
  purple: "#6D4AFF",
  ink: "#24364B",
  muted: "#61758A",
  canvas: "#F4F7FB",
  wallpaper: "#E8EEF5",
  border: "#DCE5EF",
  white: "#FFFFFF",
  band: "#F7F9FC"
};

const measureColors = {
  "Operations Daily.Cases Opened": colors.blue,
  "Operations Daily.Cases Closed": colors.teal,
  "Operations Daily.Backlog EOD": colors.amber,
  "Operations Daily.Current Backlog EOD": colors.amber,
  "SLA Compliance.SLA Compliance Percentage": colors.teal,
  "Integration Reliability.Delivery Success Percentage": colors.teal,
  "Integration Reliability.Retry Percentage": colors.amber,
  "ETL Run History.Rows Loaded": colors.teal,
  "Sum(ETL Run History.Rows Extracted)": colors.blue,
  "Sum(ETL Run History.Rows Loaded Value)": colors.teal,
  "Sum(Integration Reliability.Delivery Count)": colors.blue,
  "Sum(Integration Reliability.Successful Deliveries)": colors.teal
};

function visualId(pageName, key) {
  return crypto
    .createHash("sha256")
    .update(`${pageName}:${key}`)
    .digest("hex")
    .slice(0, 20);
}

function writeJson(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function literal(value) {
  return { expr: { Literal: { Value: value } } };
}

function bool(value) {
  return literal(value ? "true" : "false");
}

function decimal(value) {
  return literal(`${value}D`);
}

function integer(value) {
  return literal(`${value}L`);
}

function text(value) {
  return literal(`'${value.replaceAll("'", "''")}'`);
}

function fill(value) {
  return {
    solid: {
      color: literal(`'${value}'`)
    }
  };
}

function column(table, property) {
  return {
    Column: {
      Expression: { SourceRef: { Entity: table } },
      Property: property
    }
  };
}

function measure(table, property) {
  return {
    Measure: {
      Expression: { SourceRef: { Entity: table } },
      Property: property
    }
  };
}

function aggregation(table, property, fn = 0) {
  return {
    Aggregation: {
      Expression: column(table, property),
      Function: fn
    }
  };
}

function columnProjection(table, property) {
  return {
    field: column(table, property),
    queryRef: `${table}.${property}`,
    nativeQueryRef: property
  };
}

function measureProjection(table, property) {
  return {
    field: measure(table, property),
    queryRef: `${table}.${property}`,
    nativeQueryRef: property
  };
}

function aggregationProjection(table, property, fn = 0) {
  const functionNames = [
    "Sum",
    "Average",
    "Count",
    "Min",
    "Max",
    "CountNonNull",
    "Median",
    "StandardDeviation",
    "Variance"
  ];
  const nativePrefixes = [
    "Sum of",
    "Average of",
    "Count of",
    "Minimum of",
    "Maximum of",
    "Count of",
    "Median of",
    "Standard deviation of",
    "Variance of"
  ];
  return {
    field: aggregation(table, property, fn),
    queryRef: `${functionNames[fn]}(${table}.${property})`,
    nativeQueryRef: `${nativePrefixes[fn]} ${property}`
  };
}

function position(x, y, width, height, order) {
  return {
    x,
    y,
    z: order * 1000,
    height,
    width,
    tabOrder: order * 1000
  };
}

function commonContainer(titleValue, options = {}) {
  const titleVisible = Boolean(titleValue);
  return {
    title: [
      {
        properties: {
          show: bool(titleVisible),
          ...(titleVisible
            ? {
                text: text(titleValue),
                heading: text("Heading3"),
                fontColor: fill(colors.navy),
                fontSize: decimal(12),
                bold: bool(true),
                fontFamily: text("Segoe UI Semibold")
              }
            : {})
        }
      }
    ],
    subTitle: [{ properties: { show: bool(false) } }],
    background: [
      {
        properties: {
          show: bool(options.background !== false),
          color: fill(options.backgroundColor ?? colors.white),
          transparency: decimal(0)
        }
      }
    ],
    border: [
      {
        properties: {
          show: bool(options.border !== false),
          color: fill(colors.border),
          radius: decimal(options.radius ?? 8),
          width: decimal(1)
        }
      }
    ],
    padding: [
      {
        properties: {
          top: decimal(options.padding ?? 8),
          bottom: decimal(options.padding ?? 8),
          left: decimal(options.padding ?? 8),
          right: decimal(options.padding ?? 8)
        }
      }
    ]
  };
}

function seriesFormatting(queryRefs) {
  return queryRefs
    .filter((queryRef) => measureColors[queryRef])
    .map((queryRef) => ({
      properties: { fill: fill(measureColors[queryRef]) },
      selector: { metadata: queryRef }
    }));
}

function textbox(pageName, key, titleValue, subtitleValue, pos, order) {
  const id = visualId(pageName, key);
  return {
    id,
    value: {
      $schema: visualSchema,
      name: id,
      position: position(pos.x, pos.y, pos.width, pos.height, order),
      visual: {
        visualType: "textbox",
        objects: {
          general: [
            {
              properties: {
                paragraphs: [
                  {
                    textRuns: [
                      {
                        value: titleValue,
                        textStyle: {
                          fontFamily: "Segoe UI Semibold",
                          fontSize: "24px",
                          fontWeight: "600",
                          color: colors.navy
                        }
                      }
                    ],
                    horizontalTextAlignment: "left"
                  },
                  {
                    textRuns: [
                      {
                        value: subtitleValue,
                        textStyle: {
                          fontFamily: "Segoe UI",
                          fontSize: "11px",
                          color: colors.muted
                        }
                      }
                    ],
                    horizontalTextAlignment: "left"
                  }
                ]
              }
            }
          ]
        },
        visualContainerObjects: commonContainer(null, {
          background: false,
          border: false,
          padding: 0
        })
      }
    }
  };
}

function slicer(pageName, key, table, property, label, mode, pos, order) {
  const id = visualId(pageName, key);
  return {
    id,
    value: {
      $schema: visualSchema,
      name: id,
      position: position(pos.x, pos.y, pos.width, pos.height, order),
      visual: {
        visualType: "slicer",
        query: {
          queryState: {
            Values: { projections: [columnProjection(table, property)] }
          }
        },
        objects: {
          data: [{ properties: { mode: text(mode) } }],
          header: [
            {
              properties: {
                show: bool(true),
                text: text(label),
                fontFamily: text("Segoe UI Semibold"),
                textSize: decimal(10),
                fontColor: fill(colors.ink)
              }
            }
          ]
        },
        visualContainerObjects: commonContainer(null, { padding: 0 })
      }
    }
  };
}

function card(pageName, key, measures, pos, order) {
  const id = visualId(pageName, key);
  return {
    id,
    value: {
      $schema: visualSchema,
      name: id,
      position: position(pos.x, pos.y, pos.width, pos.height, order),
      visual: {
        visualType: "cardVisual",
        query: {
          queryState: {
            Data: {
              projections: measures.map(([table, property]) =>
                measureProjection(table, property)
              )
            }
          }
        },
        objects: {
          cardCalloutArea: [
            {
              properties: {
                show: bool(true),
                paddingUniform: integer(8),
                rectangleRoundedCurve: integer(8),
                backgroundFillColor: fill(colors.white),
                backgroundTransparency: decimal(0)
              }
            }
          ],
          value: [
            {
              properties: {
                show: bool(true),
                fontFamily: text("Segoe UI Semibold"),
                fontSize: decimal(24),
                bold: bool(true),
                fontColor: fill(colors.navy)
              },
              selector: { id: "default" }
            }
          ],
          label: [
            {
              properties: {
                show: bool(true),
                fontFamily: text("Segoe UI"),
                fontSize: decimal(10),
                fontColor: fill(colors.muted)
              },
              selector: { id: "default" }
            }
          ],
          outline: [
            {
              properties: { show: bool(false) },
              selector: { id: "default" }
            }
          ]
        },
        visualContainerObjects: commonContainer(null, {
          background: false,
          border: false,
          padding: 0
        })
      }
    }
  };
}

function cartesianChart(
  pageName,
  key,
  visualType,
  titleValue,
  categoryProjection,
  yProjections,
  pos,
  order,
  options = {}
) {
  const id = visualId(pageName, key);
  const allYProjections = [
    ...yProjections,
    ...(options.y2Projections ?? [])
  ];
  const queryRefs = allYProjections.map((projection) => projection.queryRef);
  const objects = {};
  const points = seriesFormatting(queryRefs);
  if (points.length) objects.dataPoint = points;
  if (allYProjections.length > 1) {
    objects.legend = [
      {
        properties: {
          show: bool(true),
          position: text("Top"),
          showTitle: bool(false),
          fontSize: decimal(9),
          labelColor: fill(colors.muted)
        }
      }
    ];
  }
  if (options.showLabels) {
    objects.labels = [{ properties: { show: bool(true) } }];
  }
  const query = {
    queryState: {
      Category: { projections: [categoryProjection] },
      Y: { projections: yProjections },
      ...(options.y2Projections
        ? { Y2: { projections: options.y2Projections } }
        : {}),
      ...(options.series
        ? { Series: { projections: [options.series] } }
        : {}),
      ...(options.tooltips
        ? { Tooltips: { projections: options.tooltips } }
        : {})
    }
  };
  if (options.sort) {
    query.sortDefinition = {
      sort: [
        {
          field: options.sort.field,
          direction: options.sort.direction
        }
      ],
      isDefaultSort: false
    };
  }
  return {
    id,
    value: {
      $schema: visualSchema,
      name: id,
      position: position(pos.x, pos.y, pos.width, pos.height, order),
      visual: {
        visualType,
        query,
        ...(Object.keys(objects).length ? { objects } : {}),
        visualContainerObjects: commonContainer(titleValue)
      }
    }
  };
}

function donutChart(
  pageName,
  key,
  titleValue,
  categoryProjection,
  valueProjection,
  pos,
  order
) {
  const id = visualId(pageName, key);
  return {
    id,
    value: {
      $schema: visualSchema,
      name: id,
      position: position(pos.x, pos.y, pos.width, pos.height, order),
      visual: {
        visualType: "donutChart",
        query: {
          queryState: {
            Category: { projections: [categoryProjection] },
            Y: { projections: [valueProjection] }
          }
        },
        objects: {
          legend: [
            {
              properties: {
                show: bool(true),
                position: text("RightCenter"),
                showTitle: bool(false),
                fontSize: decimal(9),
                labelColor: fill(colors.muted)
              }
            }
          ],
          labels: [{ properties: { show: bool(false) } }]
        },
        visualContainerObjects: commonContainer(titleValue)
      }
    }
  };
}

function tableVisual(
  pageName,
  key,
  titleValue,
  projections,
  pos,
  order,
  sort
) {
  const id = visualId(pageName, key);
  const query = {
    queryState: { Values: { projections } }
  };
  if (sort) {
    query.sortDefinition = {
      sort: [{ field: sort.field, direction: sort.direction }],
      isDefaultSort: false
    };
  }
  const container = commonContainer(titleValue);
  container.stylePreset = [
    { properties: { name: text("None") } }
  ];
  return {
    id,
    value: {
      $schema: visualSchema,
      name: id,
      position: position(pos.x, pos.y, pos.width, pos.height, order),
      visual: {
        visualType: "tableEx",
        query,
        objects: {
          columnHeaders: [
            {
              properties: {
                autoSizeColumnWidth: bool(true),
                columnAdjustment: text("growToFit"),
                fontFamily: text("Segoe UI Semibold"),
                fontSize: decimal(9),
                fontColor: fill(colors.white),
                backColor: fill(colors.navy),
                wordWrap: bool(true)
              }
            }
          ],
          values: [
            {
              properties: {
                fontFamily: text("Segoe UI"),
                fontSize: decimal(9),
                fontColorPrimary: fill(colors.ink),
                fontColorSecondary: fill(colors.ink),
                backColorPrimary: fill(colors.white),
                backColorSecondary: fill(colors.band),
                wordWrap: bool(false)
              }
            }
          ]
        },
        visualContainerObjects: container
      }
    }
  };
}

function pageBackground(pageName) {
  const filePath = path.join(pagesRoot, pageName, "page.json");
  const page = JSON.parse(fs.readFileSync(filePath, "utf8"));
  page.objects = {
    background: [
      {
        properties: {
          color: fill(colors.canvas),
          transparency: decimal(0)
        }
      }
    ],
    outspace: [
      {
        properties: {
          color: fill(colors.wallpaper),
          transparency: decimal(0)
        }
      }
    ]
  };
  writeJson(filePath, page);
}

function writePage(pageName, visuals) {
  pageBackground(pageName);
  for (const visual of visuals) {
    writeJson(
      path.join(pagesRoot, pageName, "visuals", visual.id, "visual.json"),
      visual.value
    );
  }
}

const pages = {
  OperationsOverview: [
    textbox(
      "OperationsOverview",
      "title",
      "Operations Overview",
      "Daily demand, throughput, and end-of-day workload",
      { x: 24, y: 16, width: 640, height: 60 },
      1
    ),
    slicer(
      "OperationsOverview",
      "date",
      "Date",
      "Date",
      "Date range",
      "Between",
      { x: 704, y: 16, width: 216, height: 80 },
      2
    ),
    slicer(
      "OperationsOverview",
      "category",
      "Operations Daily",
      "Category",
      "Category",
      "Dropdown",
      { x: 928, y: 16, width: 160, height: 80 },
      3
    ),
    slicer(
      "OperationsOverview",
      "status",
      "Operations Daily",
      "Status",
      "Status",
      "Dropdown",
      { x: 1096, y: 16, width: 160, height: 80 },
      4
    ),
    card(
      "OperationsOverview",
      "kpis",
      [
        ["Operations Daily", "Cases Opened"],
        ["Operations Daily", "Cases Closed"],
        ["Operations Daily", "Current Backlog EOD"],
        ["Case Aging", "Average Case Age"],
        ["Case Aging", "Median Case Age"]
      ],
      { x: 24, y: 104, width: 1232, height: 132 },
      5
    ),
    cartesianChart(
      "OperationsOverview",
      "daily-trend",
      "lineChart",
      "Daily case flow and backlog",
      columnProjection("Operations Daily", "Calendar Date"),
      [
        measureProjection("Operations Daily", "Cases Opened"),
        measureProjection("Operations Daily", "Cases Closed")
      ],
      { x: 24, y: 248, width: 780, height: 448 },
      6,
      {
        y2Projections: [measureProjection("Operations Daily", "Backlog EOD")],
        sort: {
          field: column("Operations Daily", "Calendar Date"),
          direction: "Ascending"
        }
      }
    ),
    cartesianChart(
      "OperationsOverview",
      "status-distribution",
      "clusteredBarChart",
      "Backlog by status",
      columnProjection("Operations Daily", "Status"),
      [measureProjection("Operations Daily", "Current Backlog EOD")],
      { x: 820, y: 248, width: 436, height: 216 },
      7,
      {
        showLabels: true,
        sort: {
          field: measure("Operations Daily", "Current Backlog EOD"),
          direction: "Descending"
        }
      }
    ),
    donutChart(
      "OperationsOverview",
      "category-distribution",
      "Backlog by category",
      columnProjection("Operations Daily", "Category"),
      measureProjection("Operations Daily", "Current Backlog EOD"),
      { x: 820, y: 480, width: 436, height: 216 },
      8
    )
  ],

  AgingAndSLA: [
    textbox(
      "AgingAndSLA",
      "title",
      "Aging & SLA",
      "Service risk, response time, and case-level follow-up",
      { x: 24, y: 16, width: 640, height: 60 },
      1
    ),
    slicer(
      "AgingAndSLA",
      "date",
      "Date",
      "Date",
      "Date range",
      "Between",
      { x: 704, y: 16, width: 216, height: 80 },
      2
    ),
    slicer(
      "AgingAndSLA",
      "tier",
      "Case Aging",
      "Service Tier",
      "Service tier",
      "Dropdown",
      { x: 928, y: 16, width: 160, height: 80 },
      3
    ),
    slicer(
      "AgingAndSLA",
      "category",
      "Case Aging",
      "Category",
      "Category",
      "Dropdown",
      { x: 1096, y: 16, width: 160, height: 80 },
      4
    ),
    card(
      "AgingAndSLA",
      "kpis",
      [
        ["SLA Compliance", "SLA Compliance Percentage"],
        ["SLA Compliance", "SLA Breach Count"],
        ["Case Aging", "Average Case Age"],
        ["Case Aging", "Average First Response Time"]
      ],
      { x: 24, y: 104, width: 1232, height: 132 },
      5
    ),
    cartesianChart(
      "AgingAndSLA",
      "sla-trend",
      "lineChart",
      "SLA compliance trend",
      columnProjection("SLA Compliance", "Snapshot Date"),
      [measureProjection("SLA Compliance", "SLA Compliance Percentage")],
      { x: 24, y: 248, width: 610, height: 212 },
      6,
      {
        sort: {
          field: column("SLA Compliance", "Snapshot Date"),
          direction: "Ascending"
        }
      }
    ),
    cartesianChart(
      "AgingAndSLA",
      "aging-buckets",
      "clusteredColumnChart",
      "Open cases by aging bucket",
      columnProjection("Case Aging", "Aging Bucket"),
      [aggregationProjection("Case Aging", "Case Reference", 5)],
      { x: 650, y: 248, width: 606, height: 212 },
      7,
      {
        showLabels: true,
        tooltips: [aggregationProjection("Case Aging", "Age Days", 1)],
        sort: {
          field: aggregation("Case Aging", "Age Days", 1),
          direction: "Ascending"
        }
      }
    ),
    tableVisual(
      "AgingAndSLA",
      "case-detail",
      "Cases requiring attention",
      [
        columnProjection("Case Aging", "Case Reference"),
        columnProjection("Case Aging", "Case Title"),
        columnProjection("Case Aging", "Status"),
        columnProjection("Case Aging", "Category"),
        columnProjection("Case Aging", "Priority"),
        columnProjection("Case Aging", "Service Tier"),
        columnProjection("Case Aging", "Age Days"),
        columnProjection("Case Aging", "SLA Breached")
      ],
      { x: 24, y: 476, width: 1232, height: 220 },
      8,
      { field: column("Case Aging", "Age Days"), direction: "Descending" }
    )
  ],

  IntegrationReliability: [
    textbox(
      "IntegrationReliability",
      "title",
      "Integration Reliability",
      "Delivery health, retries, dead letters, and processing latency",
      { x: 24, y: 16, width: 640, height: 60 },
      1
    ),
    slicer(
      "IntegrationReliability",
      "date",
      "Date",
      "Date",
      "Date range",
      "Between",
      { x: 704, y: 16, width: 216, height: 80 },
      2
    ),
    slicer(
      "IntegrationReliability",
      "channel",
      "Integration Reliability",
      "Channel",
      "Channel",
      "Dropdown",
      { x: 928, y: 16, width: 160, height: 80 },
      3
    ),
    slicer(
      "IntegrationReliability",
      "reason",
      "Integration Reliability",
      "Failure Reason",
      "Failure reason",
      "Dropdown",
      { x: 1096, y: 16, width: 160, height: 80 },
      4
    ),
    card(
      "IntegrationReliability",
      "kpis",
      [
        ["Integration Reliability", "Delivery Success Percentage"],
        ["Integration Reliability", "Retry Percentage"],
        ["Integration Reliability", "Dead-Letter Count"],
        ["Integration Reliability", "P95 Processing Time"]
      ],
      { x: 24, y: 104, width: 1232, height: 132 },
      5
    ),
    cartesianChart(
      "IntegrationReliability",
      "reliability-trend",
      "lineChart",
      "Delivery success and retry trend",
      columnProjection("Integration Reliability", "Calendar Date"),
      [
        measureProjection(
          "Integration Reliability",
          "Delivery Success Percentage"
        ),
        measureProjection("Integration Reliability", "Retry Percentage")
      ],
      { x: 24, y: 248, width: 780, height: 448 },
      6,
      {
        sort: {
          field: column("Integration Reliability", "Calendar Date"),
          direction: "Ascending"
        }
      }
    ),
    cartesianChart(
      "IntegrationReliability",
      "channel-volume",
      "clusteredBarChart",
      "Delivery volume by channel",
      columnProjection("Integration Reliability", "Channel"),
      [
        aggregationProjection("Integration Reliability", "Delivery Count", 0),
        aggregationProjection(
          "Integration Reliability",
          "Successful Deliveries",
          0
        )
      ],
      { x: 820, y: 248, width: 436, height: 216 },
      7,
      {
        sort: {
          field: aggregation("Integration Reliability", "Delivery Count", 0),
          direction: "Descending"
        }
      }
    ),
    donutChart(
      "IntegrationReliability",
      "failure-reasons",
      "Delivery failures by reason",
      columnProjection("Integration Reliability", "Failure Reason"),
      aggregationProjection("Integration Reliability", "Delivery Count", 0),
      { x: 820, y: 480, width: 436, height: 216 },
      8
    )
  ],

  PipelineAndDataQuality: [
    textbox(
      "PipelineAndDataQuality",
      "title",
      "Pipeline & Data Quality",
      "ETL throughput, execution health, and validation outcomes",
      { x: 24, y: 16, width: 640, height: 60 },
      1
    ),
    slicer(
      "PipelineAndDataQuality",
      "pipeline",
      "ETL Run History",
      "Pipeline Name",
      "Pipeline",
      "Dropdown",
      { x: 720, y: 16, width: 208, height: 80 },
      2
    ),
    slicer(
      "PipelineAndDataQuality",
      "status",
      "ETL Run History",
      "Status",
      "Run status",
      "Dropdown",
      { x: 936, y: 16, width: 160, height: 80 },
      3
    ),
    slicer(
      "PipelineAndDataQuality",
      "passed",
      "Data Quality",
      "Passed",
      "Check result",
      "Dropdown",
      { x: 1104, y: 16, width: 152, height: 80 },
      4
    ),
    card(
      "PipelineAndDataQuality",
      "kpis",
      [
        ["ETL Run History", "ETL Success Percentage"],
        ["ETL Run History", "Average ETL Duration"],
        ["ETL Run History", "Rows Loaded"],
        ["Data Quality", "Failed Quality Checks"]
      ],
      { x: 24, y: 104, width: 1232, height: 132 },
      5
    ),
    cartesianChart(
      "PipelineAndDataQuality",
      "pipeline-throughput",
      "clusteredColumnChart",
      "Rows extracted and loaded by pipeline",
      columnProjection("ETL Run History", "Pipeline Name"),
      [
        aggregationProjection("ETL Run History", "Rows Extracted", 0),
        aggregationProjection("ETL Run History", "Rows Loaded Value", 0)
      ],
      { x: 24, y: 248, width: 610, height: 212 },
      6,
      {
        showLabels: true,
        sort: {
          field: aggregation("ETL Run History", "Rows Loaded Value", 0),
          direction: "Descending"
        }
      }
    ),
    cartesianChart(
      "PipelineAndDataQuality",
      "quality-outcomes",
      "clusteredBarChart",
      "Quality checks by outcome",
      columnProjection("Data Quality", "Check"),
      [aggregationProjection("Data Quality", "Batch ID", 5)],
      { x: 650, y: 248, width: 606, height: 212 },
      7,
      {
        series: columnProjection("Data Quality", "Passed"),
        showLabels: true,
        sort: {
          field: aggregation("Data Quality", "Batch ID", 5),
          direction: "Descending"
        }
      }
    ),
    tableVisual(
      "PipelineAndDataQuality",
      "run-history",
      "ETL run history",
      [
        columnProjection("ETL Run History", "Batch ID"),
        columnProjection("ETL Run History", "Pipeline Name"),
        columnProjection("ETL Run History", "Started At"),
        columnProjection("ETL Run History", "Duration Ms"),
        columnProjection("ETL Run History", "Rows Loaded Value"),
        columnProjection("ETL Run History", "Status")
      ],
      { x: 24, y: 476, width: 610, height: 220 },
      8,
      {
        field: column("ETL Run History", "Started At"),
        direction: "Descending"
      }
    ),
    tableVisual(
      "PipelineAndDataQuality",
      "quality-detail",
      "Data quality detail",
      [
        columnProjection("Data Quality", "Check"),
        columnProjection("Data Quality", "Expected"),
        columnProjection("Data Quality", "Actual"),
        columnProjection("Data Quality", "Passed"),
        columnProjection("Data Quality", "Details"),
        columnProjection("Data Quality", "Executed At")
      ],
      { x: 650, y: 476, width: 606, height: 220 },
      9,
      {
        field: column("Data Quality", "Executed At"),
        direction: "Descending"
      }
    )
  ]
};

for (const [pageName, visuals] of Object.entries(pages)) {
  writePage(pageName, visuals);
}

console.log(
  `Generated ${Object.values(pages).flat().length} visuals across ${
    Object.keys(pages).length
  } Power BI pages.`
);
