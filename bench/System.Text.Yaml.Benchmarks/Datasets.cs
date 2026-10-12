using System.Text;

namespace System.Text.Yaml.Benchmarks;

/// <summary>Deterministic synthetic documents that mimic common real-world YAML shapes.</summary>
public static class Datasets
{
    /// <summary>Kubernetes-style multi-document manifest, repeated <paramref name="copies"/> times (small, many tokens).</summary>
    public static byte[] Kubernetes(int copies)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < copies; i++)
        {
            sb.Append($$"""
                ---
                apiVersion: apps/v1
                kind: Deployment
                metadata:
                  name: web-{{i}}
                  namespace: production
                  labels:
                    app: web
                    tier: frontend
                    release: "2024.{{i}}"
                  annotations:
                    description: "Front-end web server, replica set {{i}}"
                spec:
                  replicas: 3
                  selector:
                    matchLabels:
                      app: web
                  template:
                    metadata:
                      labels:
                        app: web
                    spec:
                      containers:
                        - name: nginx
                          image: nginx:1.25.{{i % 9}}
                          ports:
                            - containerPort: 80
                              protocol: TCP
                          env:
                            - name: LOG_LEVEL
                              value: info
                            - name: FEATURE_FLAGS
                              value: "a,b,c"
                          resources:
                            limits: {cpu: 500m, memory: 128Mi}
                            requests: {cpu: 250m, memory: 64Mi}
                          command: ["/bin/sh", "-c", "nginx -g 'daemon off;'"]
                ---
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: web-config-{{i}}
                data:
                  settings.ini: |
                    [server]
                    port = 8080
                    workers = 4

                    [logging]
                    level = debug
                  motd: >
                    Welcome to the
                    production cluster.
                  enabled: true
                  ratio: 0.75
                  empty: ~

                """);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>A long block sequence of records with nested mappings, sequences and scalars of every kind.</summary>
    public static byte[] Records(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append("- id: ").Append(i).Append('\n');
            sb.Append("  name: \"Customer ").Append(i).Append("\"\n");
            sb.Append("  email: user").Append(i).Append("@example.com\n");
            sb.Append("  active: ").Append(i % 2 == 0 ? "true" : "false").Append('\n');
            sb.Append("  balance: ").Append((i * 37 % 100000) / 100.0).Append('\n');
            sb.Append("  tags: [alpha, beta, gamma").Append(i % 7).Append("]\n");
            sb.Append("  address:\n");
            sb.Append("    street: ").Append(i).Append(" Main Street\n");
            sb.Append("    city: Springfield\n");
            sb.Append("    zip: '").Append(10000 + i % 90000).Append("'\n");
            sb.Append("  history:\n");
            sb.Append("    - {date: 2024-01-").Append(1 + i % 28).Append(", amount: ").Append(i % 500).Append("}\n");
            sb.Append("    - {date: 2024-02-").Append(1 + i % 28).Append(", amount: ").Append(i % 300).Append("}\n");
            sb.Append("  notes: ~\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Configuration with anchors, merge keys, comments, block scalars and long prose.</summary>
    public static byte[] Config(int services)
    {
        var sb = new StringBuilder();
        sb.Append("# Generated configuration\n");
        sb.Append("defaults: &defaults\n  restart: always\n  logging: {driver: json-file, options: {max-size: 10m, max-file: \"3\"}}\n  healthcheck:\n    interval: 30s\n    timeout: 10s\n    retries: 3\n\n");
        sb.Append("services:\n");
        for (int i = 0; i < services; i++)
        {
            sb.Append("  service").Append(i).Append(":\n");
            sb.Append("    <<: *defaults\n");
            sb.Append("    image: registry.example.com/team/service").Append(i).Append(":v1.").Append(i % 20).Append('\n');
            sb.Append("    # exposed ports\n");
            sb.Append("    ports:\n      - \"").Append(8000 + i).Append(":80\"\n      - \"").Append(9000 + i).Append(":443\"\n");
            sb.Append("    environment:\n      - KEY_").Append(i).Append("=value with spaces and: colons\n      - OTHER=").Append(i * 3).Append('\n');
            sb.Append("    command: |\n      #!/bin/sh\n      echo starting service").Append(i).Append("\n      exec ./run --port 80\n");
            sb.Append("    description: >\n      This service handles a very important part of the platform and\n      therefore needs a rather long description that spans several lines.\n");
            sb.Append("    depends_on: [db, cache, queue]\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>JSON-like data written entirely in flow style (JSON is YAML).</summary>
    public static byte[] FlowJson(int count)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("{\"id\": ").Append(i)
              .Append(", \"name\": \"item-").Append(i)
              .Append("\", \"price\": ").Append((i % 1000) / 10.0)
              .Append(", \"tags\": [\"a\", \"b\", \"c\"], \"stock\": {\"warehouse\": ").Append(i % 17)
              .Append(", \"reserved\": null, \"ok\": true}}");
            if (i % 8 == 7) sb.Append('\n');
        }
        sb.Append("]\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Large plain/quoted text values (stresses scalar scanning and folding).</summary>
    public static byte[] Prose(int paragraphs)
    {
        var sb = new StringBuilder();
        string sentence = "The quick brown fox jumps over the lazy dog while the five boxing wizards jump quickly. ";
        for (int i = 0; i < paragraphs; i++)
        {
            sb.Append("entry").Append(i).Append(":\n");
            sb.Append("  plain: ").Append(sentence).Append(sentence).Append(sentence).Append('\n');
            sb.Append("  folded plain: ").Append(sentence).Append("\n    ").Append(sentence).Append("\n    ").Append(sentence).Append('\n');
            sb.Append("  double: \"").Append(sentence).Append("with \\\"escapes\\\" and \\t tabs\\n\"\n");
            sb.Append("  single: '").Append(sentence).Append("it''s quoted'\n");
            sb.Append("  literal: |\n");
            for (int l = 0; l < 6; l++) sb.Append("    ").Append(sentence).Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
