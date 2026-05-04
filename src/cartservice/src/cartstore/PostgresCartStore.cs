// Copyright 2024 Google LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Threading.Tasks;
using Grpc.Core;
using Npgsql;
using Microsoft.Extensions.Configuration;

namespace cartservice.cartstore
{
    public class PostgresCartStore : ICartStore
    {
        private readonly NpgsqlDataSource _dataSource;
        private const string TableName = "cart_items";

        public PostgresCartStore(IConfiguration configuration)
        {
            var connectionString = configuration["POSTGRES_CONN_STRING"];
            _dataSource = NpgsqlDataSource.Create(connectionString);
            InitializeSchema().GetAwaiter().GetResult();
        }

        private async Task InitializeSchema()
        {
            await using var cmd = _dataSource.CreateCommand(
                $"CREATE TABLE IF NOT EXISTS {TableName} (" +
                "userId TEXT NOT NULL, " +
                "productId TEXT NOT NULL, " +
                "quantity INT NOT NULL, " +
                "PRIMARY KEY (userId, productId))");
            await cmd.ExecuteNonQueryAsync();

            await using var idxCmd = _dataSource.CreateCommand(
                $"CREATE INDEX IF NOT EXISTS idx_{TableName}_userId ON {TableName} (userId)");
            await idxCmd.ExecuteNonQueryAsync();

            Console.WriteLine("PostgresCartStore: schema initialized");
        }

        public async Task AddItemAsync(string userId, string productId, int quantity)
        {
            Console.WriteLine($"AddItemAsync called with userId={userId}, productId={productId}, quantity={quantity}");
            try
            {
                await using var cmd = _dataSource.CreateCommand(
                    $"INSERT INTO {TableName} (userId, productId, quantity) " +
                    "VALUES ($1, $2, $3) " +
                    "ON CONFLICT (userId, productId) " +
                    "DO UPDATE SET quantity = cart_items.quantity + $3");
                cmd.Parameters.AddWithValue(userId);
                cmd.Parameters.AddWithValue(productId);
                cmd.Parameters.AddWithValue(quantity);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                throw new RpcException(
                    new Status(StatusCode.FailedPrecondition, $"Can't access cart storage. {ex}"));
            }
        }

        public async Task<Hipstershop.Cart> GetCartAsync(string userId)
        {
            Console.WriteLine($"GetCartAsync called with userId={userId}");
            var cart = new Hipstershop.Cart { UserId = userId };
            try
            {
                await using var cmd = _dataSource.CreateCommand(
                    $"SELECT productId, quantity FROM {TableName} WHERE userId = $1");
                cmd.Parameters.AddWithValue(userId);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    cart.Items.Add(new Hipstershop.CartItem
                    {
                        ProductId = reader.GetString(0),
                        Quantity = reader.GetInt32(1)
                    });
                }
            }
            catch (Exception ex)
            {
                throw new RpcException(
                    new Status(StatusCode.FailedPrecondition, $"Can't access cart storage. {ex}"));
            }
            return cart;
        }

        public async Task EmptyCartAsync(string userId)
        {
            Console.WriteLine($"EmptyCartAsync called with userId={userId}");
            try
            {
                await using var cmd = _dataSource.CreateCommand(
                    $"DELETE FROM {TableName} WHERE userId = $1");
                cmd.Parameters.AddWithValue(userId);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                throw new RpcException(
                    new Status(StatusCode.FailedPrecondition, $"Can't access cart storage. {ex}"));
            }
        }

        public bool Ping()
        {
            try
            {
                using var cmd = _dataSource.CreateCommand("SELECT 1");
                cmd.ExecuteScalar();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
